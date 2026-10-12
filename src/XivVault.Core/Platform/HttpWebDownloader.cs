using System.Net;
using System.Net.Sockets;

namespace XivVault.Core.Platform;

/// <summary>
/// Downloads plugin lists and plugins over HTTPS. A network failure becomes a <see cref="WebDownloadException"/>
/// with a fixed reason, since the exceptions underneath name the host and port. An error writing the destination,
/// such as a full disk, reaches the caller unchanged, so callers catch everything for each plugin.
/// </summary>
public sealed class HttpWebDownloader : IWebDownloader, IDisposable
{
    private const int MaxRedirects = 5;
    private const string TimedOut = "the download timed out";
    private const string TooLarge = "the download is larger than XIV Vault accepts";

    // The server must answer, and then keep sending, within this long. Nothing limits the whole
    // transfer, so a large plugin on a slow link still finishes.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    private static readonly string UserAgent = $"XIV-Vault/{XivVaultInfo.Version} (+{XivVaultInfo.RepositoryUrl})";

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;

    public HttpWebDownloader()
        : this(CreateHandler(), StallTimeout, Task.Delay, TimeProvider.System)
    {
    }

    internal HttpWebDownloader(HttpMessageHandler handler, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider clock)
    {
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout;
        _delay = delay;
        _clock = clock;
    }

    public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new WebDownloadException("the address isn't HTTPS");
        }

        // Callers run on the thread pool, and no await below needs their synchronization context.
        DownloadAsync(uri, accept, destination, maxBytes, cancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _client.Dispose();

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        // Redirects are followed by hand, so every hop is held to HTTPS.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = false,
        ConnectCallback = ConnectAsync,
    };

    // Alternates the families, IPv4 first (RFC 8305 section 4): a broken IPv6 never delays the first
    // attempt, and a blackholed IPv4 costs one address timeout before IPv6 is tried.
    internal static IEnumerable<IPAddress> Interleave(IEnumerable<IPAddress> addresses) =>
        addresses
            .GroupBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .SelectMany(family => family.Select((address, place) => (address, turn: (place * 2) + family.Key)))
            .OrderBy(entry => entry.turn)
            .Select(entry => entry.address);

    private async Task DownloadAsync(Uri uri, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var start = destination.Position;
        for (var retry = 0; ; retry++)
        {
            destination.SetLength(start);
            destination.Position = start;
            var failure = await AttemptAsync(uri, accept, destination, maxBytes, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return;
            }

            if (retry == RetryDelays.Length)
            {
                throw new WebDownloadException(failure.Reason);
            }

            await _delay(failure.RetryAfter ?? RetryDelays[retry], cancellationToken).ConfigureAwait(false);
        }
    }

    // Null once the body is in the destination, or a failure that a retry might fix. Failures a
    // retry can't fix are thrown, and OperationCanceledException only for the caller's token.
    private async Task<Failure?> AttemptAsync(Uri uri, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept);

            // As Dalamud's installer does: only a plugin list, which callers ask for as JSON, bypasses caches.
            if (accept == "application/json")
            {
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            }

            HttpResponseMessage response;
            try
            {
                timeout.CancelAfter(_timeout);
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new Failure(ex is HttpRequestException ? "the server couldn't be reached" : TimedOut);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (redirects == MaxRedirects)
                    {
                        throw new WebDownloadException("the download was redirected too many times");
                    }

                    // Relative addresses resolve against this hop; one that can't resolve isn't HTTPS either.
                    if (!Uri.TryCreate(uri, location, out var next) || next.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new WebDownloadException("the download was redirected to an address that isn't HTTPS");
                    }

                    uri = next;
                    continue;
                }

                if (status is 429 or 500 or 502 or 503 or 504)
                {
                    return new Failure($"the server answered {status}", RetryAfter(response));
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new WebDownloadException($"the server answered {status}");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new WebDownloadException(TooLarge);
                }

                return await CopyAsync(response.Content, destination, maxBytes, timeout, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Counts what arrives rather than trusting Content-Length, which may be missing or wrong.
    private async Task<Failure?> CopyAsync(HttpContent content, Stream destination, long maxBytes, CancellationTokenSource timeout, CancellationToken cancellationToken)
    {
        var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            try
            {
                timeout.CancelAfter(_timeout);
                read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A reset, a stall or bad compressed data: only reads are caught, so an error writing
                // the caller's destination reaches the caller as it is.
                cancellationToken.ThrowIfCancellationRequested();
                return new Failure(ex is OperationCanceledException ? TimedOut : "the download was interrupted");
            }

            if (read == 0)
            {
                return null;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new WebDownloadException(TooLarge);
            }

            destination.Write(buffer, 0, read);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date - _clock.GetUtcNow());
        return wait is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, MaxRetryAfter.Ticks)) : null;
    }

    // .NET 10 tries a host's addresses one at a time in DNS order, IPv6 first on a PC that has it,
    // each for as long as the system waits (about 21 seconds on Windows). Where IPv6 is broken, that
    // stalls every download from raw.githubusercontent.com, which serves the official plugins over
    // both. Not needed once SocketsHttpHandler races the two (dotnet/runtime#26177).
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in Interleave(addresses))
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(AddressTimeout);
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private sealed record Failure(string Reason, TimeSpan? RetryAfter = null);
}
