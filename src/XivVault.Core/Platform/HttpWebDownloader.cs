namespace XivVault.Core.Platform;

public sealed class HttpWebDownloader : IWebDownloader
{
    public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
