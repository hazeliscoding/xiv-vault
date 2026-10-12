using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core.Platform;
using XivVault.Core.Plugins;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>
/// The installer on its own: what can change between downloading a plugin and writing it, after the settings
/// are restored, and failures it must survive without losing or leaving anything.
/// </summary>
public class PluginInstallerTests : IDisposable
{
    private static readonly (string Name, Guid Id) Wotsit = ("Wotsit", Guid.Parse("1d0b8f5e-0a8c-4c39-9f3a-2b7c0f6a1e01"));
    private static readonly (string Name, Guid Id) Orchestrion = ("Orchestrion", Guid.Parse("2e1c9a6f-1b9d-4d4a-8a4b-3c8d1a7b2f02"));
    private static readonly (string Name, Guid Id) Tippy = ("Tippy", Guid.Parse("6c50dea3-5fd1-4b8e-8e8f-70c15ebf6d06"));

    private readonly TestHost _host = new();

    public PluginInstallerTests()
    {
        Directory.CreateDirectory(Root);
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => _host.Environment.XivLauncherPath;

    private string InstalledPlugins => Path.Combine(Root, "installedPlugins");

    public void Dispose() => _host.Dispose();

    /// <summary>Downloads and checks the plugins from the official repository, as a restore does before changing anything.</summary>
    private PreparedPlugins Prepare(PluginInstaller installer, params (string Name, Guid Id)[] plugins)
    {
        var repository = new FakePluginRepository(_host.Downloads, Path.Combine(_host.Environment.Root, "web"));
        foreach (var plugin in plugins)
        {
            repository.Add(plugin.Name);
        }

        var config = Path.Combine(_host.Environment.Root, "dalamudConfig.json");
        File.WriteAllText(config, FakePluginRepository.DalamudConfig(plugins));
        return installer.Prepare(config, Root, plugin => true, [], Path.Combine(_host.Environment.Root, "work"), null, Ct);
    }

    [Theory]
    [InlineData("ffxiv_dx11")]
    [InlineData("XIVLauncher")]
    public void The_game_or_XIVLauncher_starting_while_plugins_are_written_stops_the_rest(string process)
    {
        var installer = _host.Get<PluginInstaller>();
        var prepared = Prepare(installer, Wotsit, Orchestrion, Tippy);
        var written = 0;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == RestoreStage.InstallingPlugins && ++written == 2)
            {
                _host.Processes.Running.Add(process);
            }
        });

        var report = installer.Place(prepared, progress);

        var installed = Assert.Single(report.Installed);
        Assert.Equal(2, report.NotInstalled.Count);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.GameStarted, plugin.Outcome));
        Assert.Equal([installed], Directory.GetDirectories(InstalledPlugins).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(InstalledPlugins, installed, "1.0.0.0", installed + ".json")));
    }

    [Fact]
    public void Plugin_folders_that_appeared_after_downloading_are_left_alone()
    {
        var installer = _host.Get<PluginInstaller>();
        var prepared = Prepare(installer, Wotsit, Orchestrion, Tippy);
        var wotsit = Path.Combine(InstalledPlugins, Wotsit.Name);
        Directory.CreateDirectory(wotsit);
        File.WriteAllText(Path.Combine(wotsit, "notes.txt"), "mine");
        var elsewhere = Path.Combine(_host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        RestoreHardeningTests.CreateJunction(Path.Combine(InstalledPlugins, Orchestrion.Name), elsewhere);

        var report = installer.Place(prepared, null);

        Assert.Equal([Tippy.Name], report.Installed);
        Assert.Equal(
            [new PluginOutcome(Orchestrion.Name, PluginInstallOutcome.LinkedFolder), new PluginOutcome(Wotsit.Name, PluginInstallOutcome.InTheWay)],
            report.NotInstalled.OrderBy(plugin => plugin.InternalName, StringComparer.Ordinal));
        Assert.Equal(["notes.txt"], Directory.GetFileSystemEntries(wotsit).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void A_plugins_folder_that_became_a_link_is_never_written_through()
    {
        var installer = _host.Get<PluginInstaller>();
        var prepared = Prepare(installer, Wotsit);
        var elsewhere = Path.Combine(_host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        RestoreHardeningTests.CreateJunction(InstalledPlugins, elsewhere);

        var report = installer.Place(prepared, null);

        Assert.Equal([new PluginOutcome(Wotsit.Name, PluginInstallOutcome.LinkedFolder)], report.NotInstalled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void A_plugin_that_cannot_be_written_removes_only_what_it_wrote()
    {
        var installer = _host.Get<PluginInstaller>();

        // An uninstall can leave an empty folder, and installing into it is fine.
        var orchestrion = Path.Combine(InstalledPlugins, Orchestrion.Name);
        Directory.CreateDirectory(orchestrion);
        var prepared = Prepare(installer, Wotsit, Orchestrion, Tippy);
        var staged = prepared.Staged.Single(plugin => plugin.Install.Plugin.InternalName == Orchestrion.Name);

        // Something holds the staged manifest, which is written last, so every other file is already copied when it fails.
        PluginInstallReport report;
        using (File.Open(Path.Combine(staged.Folder, Orchestrion.Name + ".json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = installer.Place(prepared, null);
        }

        Assert.Equal([Tippy.Name, Wotsit.Name], report.Installed.Order(StringComparer.Ordinal));
        Assert.Equal([new PluginOutcome(Orchestrion.Name, PluginInstallOutcome.WriteFailed)], report.NotInstalled);
        Assert.True(Directory.Exists(orchestrion));
        Assert.Empty(Directory.EnumerateFileSystemEntries(orchestrion));
    }

    [Fact]
    public void A_file_held_for_a_moment_is_written_on_a_later_attempt()
    {
        var installer = _host.Get<PluginInstaller>();
        var prepared = Prepare(installer, Wotsit);

        // An antivirus scan holds the staged manifest, which is written last, and lets go of it a moment later.
        using var held = File.Open(Path.Combine(prepared.Staged.Single().Folder, Wotsit.Name + ".json"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var release = new Timer(state => held.Dispose(), null, TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan);

        var report = installer.Place(prepared, null);

        Assert.Equal([Wotsit.Name], report.Installed);
        Assert.True(File.Exists(Path.Combine(InstalledPlugins, Wotsit.Name, "1.0.0.0", Wotsit.Name + ".json")));
    }

    [Fact]
    public void A_full_disk_fails_every_later_plugin_without_downloading_it()
    {
        var fullDisk = new FullDisk(_host.Downloads);
        var installer = new PluginInstaller(fullDisk, _host.Get<GameProcessGuard>(), NullLogger<PluginInstaller>.Instance);
        var prepared = Prepare(installer, Wotsit, Orchestrion, Tippy);

        var report = installer.Place(prepared, null);

        Assert.Empty(report.Installed);
        Assert.Equal(3, report.NotInstalled.Count);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.WriteFailed, plugin.Outcome));
        Assert.Equal(1, fullDisk.Packages);
        Assert.False(Directory.Exists(InstalledPlugins));
    }

    private sealed class InlineProgress(Action<RestoreProgress> report) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => report(value);
    }

    /// <summary>Serves the plugin lists, then fails every plugin download the way Windows does on a full disk.</summary>
    private sealed class FullDisk(FakeWebDownloader inner) : IWebDownloader
    {
        public int Packages { get; private set; }

        public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
        {
            if (url == FakePluginRepository.Official)
            {
                inner.Download(url, accept, destination, maxBytes, cancellationToken);
                return;
            }

            Packages++;
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }
    }
}
