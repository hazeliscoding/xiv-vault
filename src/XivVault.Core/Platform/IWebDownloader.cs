namespace XivVault.Core.Platform;

/// <summary>Downloads from the plugin repositories. Tests replace it so they never touch the network.</summary>
public interface IWebDownloader
{
    /// <summary>
    /// Writes the body of an HTTPS GET into <paramref name="destination"/>, following redirects to HTTPS
    /// addresses only. Throws <see cref="WebDownloadException"/> when it fails, and
    /// <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>A download that failed, with a fixed reason that never holds an address, a path or server text.</summary>
public sealed class WebDownloadException(string reason) : Exception(reason);
