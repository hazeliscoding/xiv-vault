using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Velopack.Locators;
using Velopack.Sources;
using XivVault.Core;
using XivVault.Core.Configuration;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;
using XivVault.Tests.Support;

namespace XivVault.Tests.Desktop;

public class BetaUpdateTests
{
    private const string StableFeed = "https://github.com/hazeliscoding/xiv-vault/releases/download/v1.0.1/releases.win.json";
    private const string BetaFeed = "https://github.com/hazeliscoding/xiv-vault/releases/download/v1.1.0-beta.2/releases.win.json";

    private const string GitHubReleases = $$"""
        [
          {"name":"1.1.0-beta.2","prerelease":true,"published_at":"2026-10-20T12:00:00Z","assets":[{"name":"releases.win.json","browser_download_url":"{{BetaFeed}}"}]},
          {"name":"1.0.1","prerelease":false,"published_at":"2026-10-12T12:00:00Z","assets":[{"name":"releases.win.json","browser_download_url":"{{StableFeed}}"}]}
        ]
        """;

    [Theory]
    [InlineData("1.0.0", false, "1.0.1", "1.0.1")]
    [InlineData("1.0.0", true, "1.0.1", "1.1.0-beta.2")]
    [InlineData("1.1.0-beta.1", false, "1.0.1", null)]
    [InlineData("1.1.0-beta.1", true, "1.0.1", "1.1.0-beta.2")]
    [InlineData("1.1.0-beta.2", false, "1.1.0", "1.1.0")]
    [InlineData("1.1.0-beta.2", true, "1.1.0", "1.1.0")]
    public async Task The_update_feed_obeys_the_opt_in_without_downgrading(string installed, bool beta, string stable, string? expected)
    {
        using var env = new TestEnvironment();
        var feed = Path.Combine(env.Root, "feed");
        Directory.CreateDirectory(feed);
        File.WriteAllText(Path.Combine(feed, "releases.win.json"), $$"""
            {"Assets":[
              {"PackageId":"XivVault","Version":"{{stable}}","Type":"Full","FileName":"stable.nupkg","SHA1":"abc","Size":1},
              {"PackageId":"XivVault","Version":"1.1.0-beta.2","Type":"Full","FileName":"beta.nupkg","SHA1":"def","Size":1}
            ]}
            """);
        var manager = VelopackUpdater.CreateManager(feed, beta, locator: Installed(env, installed));

        var update = await manager.CheckForUpdatesAsync();

        Assert.Equal(expected, update?.TargetFullRelease.Version.ToString());
    }

    [Theory]
    [InlineData("1.0.0", false, "1.0.1")]
    [InlineData("1.0.0", true, "1.1.0-beta.2")]
    [InlineData("1.1.0-beta.1", false, null)]
    [InlineData("1.1.0-beta.1", true, "1.1.0-beta.2")]
    public async Task GitHub_prereleases_are_offered_only_with_the_switch_on(string installed, bool beta, string? expected)
    {
        using var env = new TestEnvironment();
        var github = new FakeGitHub();
        github.Files["https://api.github.com/repos/hazeliscoding/xiv-vault/releases"] = GitHubReleases;
        github.Files[StableFeed] = Feed("1.0.1");
        github.Files[BetaFeed] = Feed("1.1.0-beta.2");
        var manager = VelopackUpdater.CreateManager(source: null, beta, github, Installed(env, installed));

        var update = await manager.CheckForUpdatesAsync();

        Assert.Equal(expected, update?.TargetFullRelease.Version.ToString());
        Assert.Equal(beta, github.Requests.Contains(BetaFeed));
    }

    [Theory]
    [InlineData("1.1.0", "XIV Vault 1.1.0", false)]
    [InlineData("1.1.0-beta.1", "Beta\n1.1.0-beta.1", true)]
    public void Only_a_beta_is_labelled_as_one_in_the_sidebar_and_Settings(string version, string sidebar, bool beta)
    {
        using var host = new DesktopTestHost();
        var services = host.Get<IServiceProvider>();
        var edition = new XivVaultEdition(version);
        var updates = ActivatorUtilities.CreateInstance<UpdatesViewModel>(services, edition);
        var main = ActivatorUtilities.CreateInstance<MainWindowViewModel>(services, edition, updates);

        Assert.Equal(sidebar, main.VersionLabel);
        Assert.Equal(beta, main.ProtectionText.Contains("This beta", StringComparison.Ordinal));
        Assert.Equal("XIV Vault " + version, updates.VersionText);
        Assert.Equal(beta, updates.IsBeta);
    }

    [Fact]
    public async Task Turning_betas_off_removes_an_offer_and_requires_a_fresh_check()
    {
        using var host = new DesktopTestHost();
        var settings = host.Get<SettingsViewModel>();
        settings.Load();
        Assert.False(settings.GetBetaVersions);
        settings.GetBetaVersions = true;
        Assert.True(host.Get<IConfigStore>().Load().GetBetaVersions);
        host.Updater.Latest = "1.1.0-beta.1";
        await settings.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.True(settings.Updates.HasUpdate);
        Assert.True(host.Updater.LastBetaPreference);

        settings.GetBetaVersions = false;

        Assert.False(settings.Updates.HasUpdate);
        Assert.False(settings.Updates.UpdateAndRestartCommand.CanExecute(null));
        Assert.True(settings.Updates.CheckNowCommand.CanExecute(null));
        Assert.False(host.Get<IConfigStore>().Load().GetBetaVersions);
        host.Updater.Latest = null;
        await settings.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.False(host.Updater.LastBetaPreference);
    }

    [Fact]
    public async Task Turning_betas_off_during_a_download_prevents_installing_it()
    {
        using var host = new DesktopTestHost();
        var store = host.Get<IConfigStore>();
        store.Save(store.Load() with { GetBetaVersions = true });
        host.Updater.Latest = "1.1.0-beta.1";
        var updates = host.Get<UpdatesViewModel>();
        await updates.CheckNowCommand.ExecuteAsync(null);
        host.Updater.DuringDownload = () => store.Save(store.Load() with { GetBetaVersions = false });

        await updates.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Null(host.Updater.RestartedInto);
        Assert.False(updates.HasUpdate);
    }

    private static TestVelopackLocator Installed(TestEnvironment env, string version) =>
        new("XivVault", version, Path.Combine(env.Root, "packages"));

    private static string Feed(string version) =>
        $$"""{"Assets":[{"PackageId":"XivVault","Version":"{{version}}","Type":"Full","FileName":"XivVault-{{version}}-full.nupkg","SHA1":"abc","Size":1}]}""";

    /// <summary>Serves the bodies a test registers, by URL without its query string, and answers 404 for anything else.</summary>
    private sealed class FakeGitHub : IFileDownloader
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Requests.Add(url);
            // The release list's page size is Velopack's choice, so it isn't part of the match.
            return Files.TryGetValue(url.Split('?')[0], out var body)
                ? Task.FromResult(body)
                : Task.FromException<string>(new HttpRequestException("the server answered 404"));
        }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Encoding.UTF8.GetBytes(await DownloadString(url, headers, timeout));

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new NotSupportedException("These tests only check for updates.");
    }
}
