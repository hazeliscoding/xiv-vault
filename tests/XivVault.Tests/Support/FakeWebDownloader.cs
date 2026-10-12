using XivVault.Core.Platform;

namespace XivVault.Tests.Support;

/// <summary>
/// Serves the bodies a test registers, by exact URL, and answers 404 for anything else, so plugin
/// installs run without the network.
/// </summary>
public sealed class FakeWebDownloader : IWebDownloader
{
    public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);

    /// <summary>URLs whose download writes this many bytes of the body and then fails, as a lost connection does.</summary>
    public Dictionary<string, int> Interruptions { get; } = new(StringComparer.Ordinal);

    /// <summary>Every URL asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Runs before each request is served: a test can start the game here, or throw to fail one URL.</summary>
    public Action<string>? BeforeRequest { get; set; }

    public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(url);
        BeforeRequest?.Invoke(url);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Bodies.TryGetValue(url, out var body))
        {
            throw new WebDownloadException("the server answered 404");
        }

        // The real downloader streams the body, so a failure can leave part of it in the destination.
        if (Interruptions.TryGetValue(url, out var written))
        {
            destination.Write(body, 0, written);
            throw new WebDownloadException("the download was interrupted");
        }

        if (body.Length > maxBytes)
        {
            destination.Write(body, 0, (int)maxBytes);
            throw new WebDownloadException("the download is larger than XIV Vault accepts");
        }

        destination.Write(body);
    }
}
