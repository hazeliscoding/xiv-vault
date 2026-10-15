using XivVault.Core.Plugins;
using XivVault.Core.Restore;

namespace XivVault.Screenshots;

// Supplies beta screen examples while the real restore still uses only the fake profile and no network.
internal sealed class BetaRestorePreview(IRestoreService inner) : IRestoreService
{
    public async Task<RestorePreview> PreviewAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        (await inner.PreviewAsync(archivePath, source, cancellationToken).ConfigureAwait(false)) with
        {
            Plugins = new PluginInstallPreview(["Wotsit", "Orchestrion", "Moodles"],
                [new CustomRepository("https://plugins.example.org/repo.json", "plugins.example.org")], true),
        };

    public Task<IReadOnlyList<SafetyCheck>> CheckAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        inner.CheckAsync(archivePath, source, cancellationToken);

    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) =>
        (await inner.RestoreAsync(request with { InstallPlugins = false }, progress, cancellationToken).ConfigureAwait(false)) with
        {
            Plugins = new PluginInstallReport(["Orchestrion", "Wotsit"], [new PluginOutcome("Moodles", PluginInstallOutcome.NotOffered)], 2),
        };
}
