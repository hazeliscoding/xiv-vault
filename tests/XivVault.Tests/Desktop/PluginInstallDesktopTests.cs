using XivVault.Core.Plugins;
using XivVault.Desktop.ViewModels;
using XivVault.Tests.Support;

namespace XivVault.Tests.Desktop;

public class PluginInstallDesktopTests
{
    private const string Custom = "https://plugins.example.org/repo.json?token=private";

    /// <summary>
    /// Reviews a backup of this PC whose plugins, Wotsit and Moodles unless others are given, come from these repositories
    /// and are all missing.
    /// </summary>
    private static async Task<RestoreViewModel> ReviewAsync(DesktopTestHost host, params (string Name, string Repository)[] plugins)
    {
        plugins = plugins.Length > 0 ? plugins : [("Wotsit", FakePluginRepository.Official), ("Moodles", Custom)];
        var launcher = host.CreateLauncher();
        File.WriteAllText(Path.Combine(launcher.DataPath, "dalamudConfig.json"), FakePluginRepository.DalamudConfig(
            plugins.Select(plugin => (plugin.Name, Guid.NewGuid())),
            repositories: plugins.Select(plugin => plugin.Repository).Where(url => url != FakePluginRepository.Official).Distinct().Select(url => (url, true))));
        FakeDalamud.InstallDalamud(launcher.Root, new Version(15, 0, 3, 6));
        await host.BackUpAsync();
        var repo = new FakePluginRepository(host.Downloads, Path.Combine(host.Environment.Root, "web"));
        foreach (var (name, repository) in plugins)
        {
            repo.Add(name, repository: repository);
        }

        await host.Session.RefreshAsync();
        var restore = host.Get<RestoreViewModel>();
        restore.Begin(null);
        await restore.ContinueCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep2);
        return restore;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_plugins_install_only_when_their_repository_is_chosen(bool ticked)
    {
        using var host = new DesktopTestHost(pluginInstalls: true);
        var restore = await ReviewAsync(host);
        Assert.True(restore.ShowPluginInstalls);
        Assert.True(restore.InstallMissingPlugins);
        var repository = Assert.Single(restore.RepositoryChoices);
        Assert.False(repository.IsChosen);
        Assert.Equal("plugins.example.org", repository.Host);
        Assert.DoesNotContain("private", repository.Host);
        Assert.Empty(host.Downloads.Requests);
        repository.IsChosen = ticked;

        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep4);
        var report = Assert.IsType<PluginInstallReport>(restore.Result?.Plugins);
        Assert.Contains("Wotsit", report.Installed);
        Assert.Equal(ticked, report.Installed.Contains("Moodles"));
        Assert.Equal(ticked, host.Downloads.Requests.Contains(Custom));
        Assert.Contains("Wotsit", restore.InstalledPluginsText);
        Assert.Equal(!ticked, restore.HasUninstalledPlugins);
    }

    [Fact]
    public async Task Repositories_at_one_host_share_one_row_and_ticking_it_requests_each_of_them()
    {
        const string sameHost = "https://Plugins.Example.org/more/repo.json";
        const string other = "https://other.example.net/repo.json";
        using var host = new DesktopTestHost(pluginInstalls: true);
        var restore = await ReviewAsync(host, ("Moodles", Custom), ("Lifestream", sameHost), ("Tippy", other));

        Assert.Equal(["plugins.example.org · 2 repositories", "other.example.net · 1 repository"], restore.RepositoryChoices.Select(choice => choice.Label));
        restore.RepositoryChoices[0].IsChosen = true;
        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.Contains(Custom, host.Downloads.Requests);
        Assert.Contains(sameHost, host.Downloads.Requests);
        Assert.DoesNotContain(other, host.Downloads.Requests);
        Assert.Equal(["Lifestream", "Moodles"], restore.Result?.Plugins?.Installed.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Stable_versions_and_restores_without_install_consent_never_download(bool beta, bool install, bool dalamud)
    {
        using var host = new DesktopTestHost(pluginInstalls: beta);
        var restore = await ReviewAsync(host);
        Assert.Equal(beta, restore.ShowPluginInstalls);
        restore.InstallMissingPlugins = install;
        restore.DalamudSettingsChosen = dalamud;
        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep4);
        Assert.Empty(host.Downloads.Requests);
        Assert.Null(restore.Result?.Plugins);
        Assert.False(restore.HasPluginInstallReport);
    }

    [Fact]
    public async Task The_safety_snapshot_is_never_shown_complete_before_downloads()
    {
        using var host = new DesktopTestHost(pluginInstalls: true);
        var restore = await ReviewAsync(host);
        var stagesBeforeDownloads = new List<StageItem>();
        restore.Stages.CollectionChanged += (_, args) =>
        {
            if (host.Downloads.Requests.Count == 0 && args.NewItems is { } items)
            {
                stagesBeforeDownloads.AddRange(items.Cast<StageItem>());
            }
        };
        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep4);
        Assert.DoesNotContain(stagesBeforeDownloads, item => item.Label == "Creating pre-restore safety backup" && item.State == StepState.Done);
    }
}
