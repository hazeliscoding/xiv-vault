using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using XivVault.Core.Configuration;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;
using XivVault.Tests.Support;

namespace XivVault.Tests.Desktop;

public class BetaUpdateTests
{
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
        var source = new ChannelUpdateSource(new SimpleFileSource(new DirectoryInfo(feed)), beta);
        var manager = new UpdateManager(source, new UpdateOptions { AllowVersionDowngrade = false },
            new TestVelopackLocator("XivVault", installed, Path.Combine(env.Root, "packages")));

        var update = await manager.CheckForUpdatesAsync();

        Assert.Equal(expected, update?.TargetFullRelease.Version.ToString());
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
}
