namespace XivVault.Core.Platform;

/// <summary>Downloads from the plugin repositories. Tests replace it so they never touch the network.</summary>
public interface IWebDownloader
{
    /// <summary>
    /// Writes the body of an HTTPS GET into <paramref name="destination"/>, following redirects to HTTPS
    /// addresses only. A plugin list is asked for as <c>application/json</c>, which bypasses caches, and a
    /// plugin as <c>application/zip</c>. A network failure throws <see cref="WebDownloadException"/> with a
    /// fixed reason, and <see cref="OperationCanceledException"/> comes only from <paramref name="cancellationToken"/>.
    /// An error writing <paramref name="destination"/>, such as a full disk, reaches the caller unchanged and
    /// can name a path, so callers catch everything for each plugin. A failure can leave part of a body behind.
    /// </summary>
    void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>A download that failed, with a fixed reason that never holds an address, a path or server text.</summary>
public sealed class WebDownloadException(string reason) : Exception(reason);
