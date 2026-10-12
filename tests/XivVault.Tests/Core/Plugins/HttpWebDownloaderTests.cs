using System.Diagnostics;
using System.Net;
using System.Text;
using XivVault.Core;
using XivVault.Core.Platform;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>Plugin downloads, against a fake server so no test touches the network.</summary>
public class HttpWebDownloaderTests
{
    private const string Start = "https://plugins.example.com/Plugin/Download/Tester?isTesting=False";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly List<TimeSpan> _delays = [];

    private delegate Task<HttpResponseMessage> Answer(CancellationToken cancellationToken);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Sends_a_plain_http11_get_that_names_xiv_vault_and_nothing_about_the_user()
    {
        var server = new FakeServer(Ok("zip"));
        using var downloader = Create(server);

        downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(HttpVersion.Version11, request.Version);
        Assert.Equal(Start, request.RequestUri!.AbsoluteUri);
        Assert.Equal($"XIV-Vault/{XivVaultInfo.Version} (+https://github.com/hazeliscoding/xiv-vault)", request.Headers.UserAgent.ToString());
        Assert.Equal("application/zip", request.Headers.Accept.ToString());
        Assert.Equal("no-cache", request.Headers.CacheControl?.ToString());
        Assert.Equal(["Accept", "Cache-Control", "User-Agent"], request.Headers.Select(header => header.Key).Order());
    }

    [Fact]
    public void Follows_up_to_five_https_redirects_of_every_kind_including_relative_ones()
    {
        var server = new FakeServer(
            Redirect("/File/Get/0a1b", HttpStatusCode.MovedPermanently),
            Redirect("https://files.example.net/stable/Tester/latest.zip", HttpStatusCode.Found),
            Redirect("latest.zip?raw=1", HttpStatusCode.SeeOther),
            Redirect("//cdn.example.org/Tester/latest.zip", HttpStatusCode.TemporaryRedirect),
            Redirect("https://cdn.example.org/Tester/1.0.0.0/latest.zip", HttpStatusCode.PermanentRedirect),
            Ok("zip"));
        using var downloader = Create(server);
        var destination = new MemoryStream();

        downloader.Download(Start, "application/zip", destination, 100, Ct);

        Assert.Equal(
            [
                Start,
                "https://plugins.example.com/File/Get/0a1b",
                "https://files.example.net/stable/Tester/latest.zip",
                "https://files.example.net/stable/Tester/latest.zip?raw=1",
                "https://cdn.example.org/Tester/latest.zip",
                "https://cdn.example.org/Tester/1.0.0.0/latest.zip",
            ],
            server.Addresses);
        Assert.Equal("zip", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Theory]
    [InlineData("http://files.example.net/Tester/latest.zip")]
    [InlineData(@"\\files.example.net\Tester\latest.zip")]
    [InlineData("//")]
    [InlineData("http:latest.zip")]
    public void Refuses_a_redirect_to_an_address_that_is_not_https(string location)
    {
        var server = new FakeServer(Redirect("/File/Get/0a1b"), Redirect(location), Ok("zip"));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the download was redirected to an address that isn't HTTPS", error.Message);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public void Refuses_a_sixth_redirect()
    {
        var server = new FakeServer(Redirect("/again"));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the download was redirected too many times", error.Message);
        Assert.Equal(6, server.Requests.Count);
    }

    [Theory]
    [InlineData("http://plugins.example.com/Plugin/PluginMaster")]
    [InlineData("ftp://plugins.example.com/Plugin/PluginMaster")]
    [InlineData("plugins.example.com/Plugin/PluginMaster")]
    [InlineData("/Plugin/PluginMaster")]
    [InlineData("")]
    public void Refuses_an_address_that_is_not_https_without_sending_anything(string url)
    {
        var server = new FakeServer(Ok("[]"));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(url, "application/json", new MemoryStream(), 100, Ct));

        Assert.Equal("the address isn't HTTPS", error.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.Found, 1)] // A redirect without an address.
    [InlineData(HttpStatusCode.InternalServerError, 3)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    public void Reports_an_answer_that_is_not_a_success_by_its_status(HttpStatusCode status, int requests)
    {
        var server = new FakeServer(Status(status));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal($"the server answered {(int)status}", error.Message);
        Assert.Equal(requests, server.Requests.Count);
    }

    [Fact]
    public void Retries_a_busy_server_and_keeps_the_answer_that_works()
    {
        var server = new FakeServer(Status(HttpStatusCode.ServiceUnavailable), Ok("zip"));
        using var downloader = Create(server);
        var destination = new MemoryStream();

        downloader.Download(Start, "application/zip", destination, 100, Ct);

        Assert.Equal("zip", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], _delays);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("3600", 30)]
    [InlineData("Mon, 12 Oct 2026 09:00:10 GMT", 10)]
    [InlineData("Mon, 12 Oct 2026 10:00:00 GMT", 30)]
    [InlineData("Mon, 12 Oct 2026 08:59:00 GMT", 0)]
    public void Waits_as_long_as_retry_after_asks_up_to_thirty_seconds(string retryAfter, int seconds)
    {
        var server = new FakeServer(Status(HttpStatusCode.TooManyRequests, retryAfter), Ok("zip"));
        using var downloader = Create(server);

        downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct);

        Assert.Equal([TimeSpan.FromSeconds(seconds)], _delays);
    }

    [Fact]
    public void Gives_up_after_two_retries()
    {
        var server = new FakeServer(Status(HttpStatusCode.ServiceUnavailable));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the server answered 503", error.Message);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _delays);
    }

    [Fact]
    public void Retries_a_server_that_could_not_be_reached()
    {
        var server = new FakeServer(Unreachable, Unreachable, Ok("zip"));
        using var downloader = Create(server);
        var destination = new MemoryStream();

        downloader.Download(Start, "application/zip", destination, 100, Ct);

        Assert.Equal("zip", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _delays);
    }

    [Fact]
    public void Refuses_a_download_that_says_it_is_too_large_without_reading_it()
    {
        var body = new Body(new byte[101]);
        var server = new FakeServer(_ => Respond(body, length: 101));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the download is larger than XIV Vault accepts", error.Message);
        Assert.Equal(0, body.Reads);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10L)]
    public void Stops_a_body_as_soon_as_it_passes_the_limit_whatever_its_length_says(long? length)
    {
        var body = new Body(new byte[1000], times: 10_000);
        var server = new FakeServer(_ => Respond(body, length));
        using var downloader = Create(server);
        var destination = new MemoryStream();

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", destination, 100_000, Ct));

        Assert.Equal("the download is larger than XIV Vault accepts", error.Message);
        Assert.Equal(101, body.Reads);
        Assert.InRange(destination.Length, 0, 100_000);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Times_out_when_the_server_stops_sending(bool afterHeaders)
    {
        var server = new FakeServer(afterHeaders ? _ => Respond(new Body("part"u8.ToArray(), stalls: true)) : Hang);
        using var downloader = Create(server, TimeSpan.FromMilliseconds(50));

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the download timed out", error.Message);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_callers_token_cancels_a_download(bool afterHeaders)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = new FakeServer(afterHeaders
            ? _ => Respond(new Body("part"u8.ToArray(), stalls: true, stalled: cancel.Cancel))
            : token =>
            {
                cancel.Cancel();
                return Hang(token);
            });
        using var downloader = Create(server);

        var error = Assert.ThrowsAny<OperationCanceledException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, cancel.Token));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Single(server.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public void A_timeout_inside_the_http_stack_fails_the_download_instead_of_cancelling_it()
    {
        // HttpClient reports its own timeouts as TaskCanceledException, which is an OperationCanceledException.
        var server = new FakeServer(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));
        using var downloader = Create(server);

        var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

        Assert.Equal("the download timed out", error.Message);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public void A_retry_writes_the_destination_again_from_where_it_started()
    {
        var server = new FakeServer(
            _ => Respond(new Body("stale"u8.ToArray(), error: new IOException("The response ended prematurely."))),
            Ok("fresh"));
        using var downloader = Create(server);
        var destination = new MemoryStream();
        destination.Write("kept:"u8);

        downloader.Download(Start, "application/zip", destination, 100, Ct);

        Assert.Equal("kept:fresh", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal([TimeSpan.FromSeconds(2)], _delays);
    }

    [Fact]
    public void Every_failure_has_a_fixed_reason_that_never_names_the_address_or_repeats_the_server()
    {
        var failures = new (Answer Answer, string Reason)[]
        {
            (Unreachable, "the server couldn't be reached"),
            (_ => throw new TaskCanceledException("Connecting to plugins.example.com:443 timed out."), "the download timed out"),
            (_ => Respond(new Body("Tester"u8.ToArray(), error: new IOException("Connection reset by files.example.net:443."))), "the download was interrupted"),
            (Status(HttpStatusCode.NotFound), "the server answered 404"),
            (Redirect("http://files.example.net/stable/Tester/latest.zip"), "the download was redirected to an address that isn't HTTPS"),
            (Redirect("/Plugin/Download/Tester"), "the download was redirected too many times"),
            (_ => Respond(new Body(new byte[101])), "the download is larger than XIV Vault accepts"),
        };

        foreach (var (answer, reason) in failures)
        {
            using var downloader = Create(new FakeServer(answer));

            var error = Assert.Throws<WebDownloadException>(() => downloader.Download(Start, "application/zip", new MemoryStream(), 100, Ct));

            Assert.Equal(reason, error.Message);
            Assert.Null(error.InnerException);
            foreach (var secret in new[] { "example", "Tester", "Plugin", "443", "reset" })
            {
                Assert.DoesNotContain(secret, error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Connects_to_ipv4_addresses_before_ipv6_ones_keeping_the_dns_order()
    {
        IPAddress[] addresses = [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::2"), IPAddress.Parse("192.0.2.2")];

        Assert.Equal([addresses[1], addresses[3], addresses[0], addresses[2]], HttpWebDownloader.IPv4First(addresses));
    }

    [Fact]
    public void The_real_connection_follows_no_redirects_by_itself_and_sends_no_cookies_or_credentials()
    {
        using var handler = HttpWebDownloader.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
    }

    private HttpWebDownloader Create(FakeServer server, TimeSpan? timeout = null) =>
        new(server, timeout ?? TimeSpan.FromSeconds(30), (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        }, new TestClock(Now));

    private static Answer Ok(string body) =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    // Servers put their own words in the reason and the body; neither may reach a message.
    private static Answer Status(HttpStatusCode status, string? retryAfter = null) => _ =>
    {
        var response = new HttpResponseMessage(status)
        {
            ReasonPhrase = "Tester isn't on plugins.example.com",
            Content = new StringContent("Tester isn't on plugins.example.com"),
        };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return Task.FromResult(response);
    };

    private static Answer Redirect(string location, HttpStatusCode status = HttpStatusCode.Found) => _ =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return Task.FromResult(response);
    };

    private static Task<HttpResponseMessage> Respond(Stream body, long? length = null)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static Task<HttpResponseMessage> Unreachable(CancellationToken cancellationToken) =>
        throw new HttpRequestException("No connection could be made because the target machine actively refused it. (plugins.example.com:443)");

    private static async Task<HttpResponseMessage> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new UnreachableException();
    }

    /// <summary>Gives each request the next answer in line, repeating the last, and keeps every request.</summary>
    private sealed class FakeServer(params Answer[] answers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public IEnumerable<string> Addresses => Requests.Select(request => request.RequestUri!.AbsoluteUri);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return answers[Math.Min(Requests.Count, answers.Length) - 1](cancellationToken);
        }
    }

    /// <summary>
    /// A response body that sends its bytes in one read, as many times as asked, then stops sending
    /// until it is cancelled (calling <c>stalled</c> first), fails, or ends.
    /// </summary>
    private sealed class Body(byte[] bytes, int times = 1, Exception? error = null, bool stalls = false, Action? stalled = null) : Stream
    {
        private int _sent;

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (_sent < times)
            {
                _sent++;
                var count = Math.Min(bytes.Length, buffer.Length);
                bytes.AsMemory(0, count).CopyTo(buffer);
                return count;
            }

            if (stalls)
            {
                stalled?.Invoke();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return error is null ? 0 : throw error;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
