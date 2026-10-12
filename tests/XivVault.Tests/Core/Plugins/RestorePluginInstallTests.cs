using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Logging;
using XivVault.Core.Platform;
using XivVault.Core.Plugins;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>
/// Plugin installs through a whole restore: what is requested and from where, what lands in installedPlugins,
/// and that nothing changes until every plugin is downloaded and checked.
/// </summary>
public class RestorePluginInstallTests
{
    private const string Ticked = "https://Raw.Example.com/Someone/Plugins/main/repo.json";
    private const string Unticked = "https://other.example.org/pluginmaster.json";
    private const string Disabled = "https://disabled.example.net/repo.json";
    private const string NotInSettings = "https://notenabled.example.com/repo.json";

    private static readonly Version Dalamud = new(15, 0, 3, 6);

    private static readonly (string Name, Guid Id) Wotsit = ("Wotsit", Guid.Parse("1d0b8f5e-0a8c-4c39-9f3a-2b7c0f6a1e01"));
    private static readonly (string Name, Guid Id) Orchestrion = ("Orchestrion", Guid.Parse("2e1c9a6f-1b9d-4d4a-8a4b-3c8d1a7b2f02"));
    private static readonly (string Name, Guid Id) SimpleTweaks = ("SimpleTweaksPlugin", Guid.Parse("3f2dab70-2cae-4e5b-9b5c-4d9e2b8c3a03"));
    private static readonly (string Name, Guid Id) Moodles = ("Moodles", Guid.Parse("4a3ebc81-3dbf-4f6c-8c6d-5eaf3c9d4b04"));
    private static readonly (string Name, Guid Id) Lifestream = ("Lifestream", Guid.Parse("5b4fcd92-4ec0-4a7d-9d7e-6fb04dae5c05"));
    private static readonly (string Name, Guid Id) Tippy = ("Tippy", Guid.Parse("6c50dea3-5fd1-4b8e-8e8f-70c15ebf6d06"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakePluginRepository Repository(TestHost host) => new(host.Downloads, Path.Combine(host.Environment.Root, "web"));

    /// <summary>Backs up another PC whose Dalamud settings are <paramref name="config"/>.</summary>
    private static async Task<string> BackUpOldPcAsync(TestHost host, string config, IEnumerable<string>? pluginConfigs = null)
    {
        var oldPc = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"), plugins: pluginConfigs);
        oldPc.Write(config, "dalamudConfig.json");
        var backup = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root }, cancellationToken: Ct);
        return backup.Record.FilePath;
    }

    /// <summary>A new PC where XIVLauncher has run once and, unless told otherwise, has finished downloading Dalamud.</summary>
    private static string NewPc(TestHost host, bool dalamudReady = true)
    {
        var root = host.Environment.XivLauncherPath;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "launcherConfigV3.json"), "{}");
        if (dalamudReady)
        {
            FakeDalamud.InstallDalamud(root, Dalamud);
        }

        return root;
    }

    /// <summary>A plugin Dalamud would load: its DLL and a manifest with its ID.</summary>
    private static void InstallLocally(string root, (string Name, Guid Id) plugin)
    {
        var folder = Path.Combine(root, "installedPlugins", plugin.Name, "0.9.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, plugin.Name + ".dll"), "binary");
        File.WriteAllText(Path.Combine(folder, plugin.Name + ".json"), $$"""{ "InternalName": "{{plugin.Name}}", "WorkingPluginId": "{{plugin.Id}}" }""");
    }

    private static Task<RestoreResult> RestoreAsync(TestHost host, string archive, params string[] customRepositories) =>
        host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true, CustomRepositories = customRepositories }, cancellationToken: Ct);

    private static string InstalledPlugins(string root) => Path.Combine(root, "installedPlugins");

    /// <summary>Every chosen plugin is in the report exactly once.</summary>
    private static PluginInstallReport Report(RestoreResult result, int chosen)
    {
        var report = Assert.IsType<PluginInstallReport>(result.Plugins);
        var named = report.Installed.Concat(report.NotInstalled.Select(plugin => plugin.InternalName)).ToList();
        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(chosen, named.Count + report.AlreadyInstalled);
        return report;
    }

    private static PluginInstallOutcome OutcomeOf(PluginInstallReport report, (string Name, Guid Id) plugin) =>
        report.NotInstalled.Single(outcome => outcome.InternalName == plugin.Name).Outcome;

    /// <summary>
    /// The zip's files are in the version folder byte for byte, apart from the manifest, which is the one Dalamud's
    /// installer writes, with the backup's ID.
    /// </summary>
    private static void AssertInstalled(string root, (string Name, Guid Id) plugin, byte[] zip, string installedFromUrl)
    {
        var folder = Path.Combine(InstalledPlugins(root), plugin.Name, "1.0.0.0");
        using var archive = new ZipArchive(new MemoryStream(zip));
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        Assert.Equal(
            entries.Select(entry => entry.FullName.Replace('/', '\\')).Order(StringComparer.Ordinal),
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Order(StringComparer.Ordinal));
        foreach (var entry in entries)
        {
            using var packaged = new MemoryStream();
            using (var input = entry.Open())
            {
                input.CopyTo(packaged);
            }

            var installed = File.ReadAllBytes(Path.Combine(folder, entry.FullName));
            if (entry.FullName == plugin.Name + ".json")
            {
                using var manifest = JsonDocument.Parse(packaged.ToArray());
                Assert.Equal(PluginManifestFile.ForInstall(manifest.RootElement, plugin.Id, testing: false, installedFromUrl, "stable", 15), installed);
            }
            else
            {
                Assert.Equal(packaged.ToArray(), installed);
            }
        }
    }

    private static void AssertNoWorkFolderLeft(TestHost host)
    {
        var work = Path.Combine(host.Environment.TempPath, "XIV Vault");
        Assert.True(!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work).Any(), "The restore's work folder is removed.");
    }

    [Fact]
    public async Task A_new_PC_gets_the_plugins_of_every_collection_with_their_IDs()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Orchestrion], savedCollection: [Orchestrion, SimpleTweaks]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        var repository = Repository(host);
        var links = new[] { Wotsit, Orchestrion, SimpleTweaks }.ToDictionary(plugin => plugin.Name, plugin => repository.Add(plugin.Name));

        var result = await RestoreAsync(host, archive);

        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        var report = Report(result, chosen: 3);
        Assert.Equal(["Orchestrion", "SimpleTweaksPlugin", "Wotsit"], report.Installed.Order(StringComparer.Ordinal));
        foreach (var plugin in new[] { Wotsit, Orchestrion, SimpleTweaks })
        {
            AssertInstalled(root, plugin, host.Downloads.Bodies[links[plugin.Name]], "OFFICIAL");
        }

        Assert.Equal(links.Values.Order(StringComparer.Ordinal), host.Downloads.Requests.Where(url => url != FakePluginRepository.Official).Order(StringComparer.Ordinal));
        AssertNoWorkFolderLeft(host);
    }

    [Fact]
    public async Task Nothing_is_requested_unless_the_restore_asks_for_plugins()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var result = await host.Restores.RestoreAsync(new RestoreRequest(archive), cancellationToken: Ct);

        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task A_stable_version_shows_nothing_about_plugins_and_never_downloads_one()
    {
        using var host = new TestHost(configure: services => services.AddSingleton(new PluginInstalls(false)));
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive);

        Assert.Null(preview.Plugins);
        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
        Assert.False(Directory.Exists(InstalledPlugins(root)));
        Assert.True(result.DalamudConfigRestored);
    }

    [Fact]
    public async Task Nothing_is_requested_when_every_plugin_is_already_installed()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        InstallLocally(root, Wotsit);
        InstallLocally(root, Orchestrion);
        Repository(host).Add(Wotsit.Name);

        var result = await RestoreAsync(host, archive);

        Assert.Empty(host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.Empty(report.Installed);
        Assert.Equal(2, report.AlreadyInstalled);
    }

    [Fact]
    public async Task Without_Dalamud_no_plugin_is_requested_and_the_settings_are_still_restored()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host, dalamudReady: false);
        Repository(host).Add(Wotsit.Name);

        var result = await RestoreAsync(host, archive);

        Assert.Empty(host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.DalamudNotReady, plugin.Outcome));
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
    }

    [Fact]
    public async Task Plugins_are_installed_only_with_the_Dalamud_settings()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var result = await host.Restores.RestoreAsync(
            new RestoreRequest(archive) { InstallPlugins = true, Selection = RestoreSelection.Only(["Artisan"], dalamudSettings: false) },
            cancellationToken: Ct);

        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task Plugins_left_out_of_the_restore_are_not_installed()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]), pluginConfigs: [Wotsit.Name, Orchestrion.Name]);
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);

        var result = await host.Restores.RestoreAsync(
            new RestoreRequest(archive) { InstallPlugins = true, Selection = RestoreSelection.Only(["wotsit"], dalamudSettings: true) },
            cancellationToken: Ct);

        var report = Report(result, chosen: 1);
        Assert.Equal([Wotsit.Name], report.Installed);
        Assert.False(Directory.Exists(Path.Combine(InstalledPlugins(root), Orchestrion.Name)));
    }

    [Fact]
    public async Task Restoring_a_safety_backup_installs_nothing()
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        launcher.Write(FakePluginRepository.DalamudConfig([Wotsit]), "dalamudConfig.json");
        FakeDalamud.InstallDalamud(launcher.Root, Dalamud);
        Repository(host).Add(Wotsit.Name);
        var backup = await host.BackUpAsync();
        var first = await host.Restores.RestoreAsync(new RestoreRequest(backup.Record.FilePath), cancellationToken: Ct);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var regular = await host.Restores.PreviewAsync(backup.Record.FilePath, cancellationToken: Ct);
        var preview = await host.Restores.PreviewAsync(first.SafetySnapshot.FilePath, cancellationToken: Ct);
        var result = await RestoreAsync(host, first.SafetySnapshot.FilePath);

        Assert.Equal([Wotsit.Name], regular.Plugins!.Missing);
        Assert.Null(preview.Plugins);
        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
    }

    [Theory]
    [InlineData("xiv-vault-2026-10-11-191924.zip")]
    [InlineData("pre-restore-2026-10-11-191926.zip")]
    public async Task Backups_from_version_0_1_0_request_nothing(string file)
    {
        using var host = new TestHost();
        var launcher = host.CreateLauncher();
        FakeDalamud.InstallDalamud(launcher.Root, Dalamud);
        Directory.CreateDirectory(host.BackupFolder);
        var archive = Path.Combine(host.BackupFolder, file);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "0.1.0", file), archive);

        var result = await RestoreAsync(host, archive);

        Assert.Empty(host.Downloads.Requests);
        Assert.True(result.DalamudConfigRestored);
    }

    [Fact]
    public async Task Only_ticked_custom_repositories_the_settings_turn_on_are_used()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig(
            [Wotsit, Moodles, Lifestream, Tippy],
            repositories: [(Ticked, true), (Unticked, true), (Disabled, false)]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        var moodles = repository.Add(Moodles.Name, repository: Ticked);
        repository.Add(Lifestream.Name, repository: Unticked);
        repository.Add(Tippy.Name, repository: Disabled);
        host.Downloads.Bodies[NotInSettings] = "[]"u8.ToArray();

        var result = await RestoreAsync(host, archive, Ticked, Disabled, NotInSettings);

        Assert.DoesNotContain(host.Downloads.Requests, url => !url.StartsWith("https://kamori.goats.dev/", StringComparison.Ordinal) && !url.StartsWith("https://raw.example.com/", StringComparison.OrdinalIgnoreCase));
        var report = Report(result, chosen: 4);
        Assert.Equal([Moodles.Name, Wotsit.Name], report.Installed.Order(StringComparer.Ordinal));
        Assert.Equal(PluginInstallOutcome.NotOffered, OutcomeOf(report, Lifestream));
        Assert.Equal(PluginInstallOutcome.NotOffered, OutcomeOf(report, Tippy));
        AssertInstalled(root, Moodles, host.Downloads.Bodies[moodles], Ticked);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(InstalledPlugins(root), Moodles.Name, "1.0.0.0", "Moodles.json")));
        Assert.Equal(Ticked, manifest.RootElement.GetProperty("InstalledFromUrl").GetString());
    }

    [Fact]
    public async Task An_unreachable_official_repository_still_restores_the_settings()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Moodles], repositories: [(Ticked, true)]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: Ticked);
        host.Downloads.Bodies.Remove(FakePluginRepository.Official);

        var result = await RestoreAsync(host, archive, Ticked);

        Assert.Equal([FakePluginRepository.Official], host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.RepositoryUnavailable, plugin.Outcome));
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task Failed_and_refused_downloads_leave_nothing_behind()
    {
        var web = new FakeWebDownloader();
        var interrupted = new InterruptedDownloads(web);
        using var host = new TestHost(configure: services => services.AddSingleton<IWebDownloader>(interrupted));
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles, Lifestream, Tippy]));
        var root = NewPc(host);
        var repository = new FakePluginRepository(web, Path.Combine(host.Environment.Root, "web"));
        repository.Add(Wotsit.Name);
        web.Bodies.Remove(repository.Add(Orchestrion.Name));
        interrupted.Url = repository.Add(Moodles.Name);
        repository.Add(Lifestream.Name, package: FakePluginRepository.Package(Lifestream.Name, version: "9.9.9.9"));
        repository.Add(Tippy.Name);

        var result = await RestoreAsync(host, archive);

        var report = Report(result, chosen: 5);
        Assert.Equal([Tippy.Name, Wotsit.Name], report.Installed.Order(StringComparer.Ordinal));
        Assert.Equal(PluginInstallOutcome.DownloadFailed, OutcomeOf(report, Orchestrion));
        Assert.Equal(PluginInstallOutcome.DownloadFailed, OutcomeOf(report, Moodles));
        Assert.Equal(PluginInstallOutcome.PackageRefused, OutcomeOf(report, Lifestream));
        foreach (var failed in new[] { Orchestrion, Moodles, Lifestream })
        {
            Assert.False(Directory.Exists(Path.Combine(InstalledPlugins(root), failed.Name)));
        }

        AssertNoWorkFolderLeft(host);
    }

    [Fact]
    public async Task The_game_starting_during_downloads_stops_the_restore_before_anything_changes()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        host.Downloads.BeforeRequest = url =>
        {
            if (url.Contains("/Plugin/Download/", StringComparison.Ordinal))
            {
                host.Processes.Running.Add("ffxiv_dx11");
            }
        };

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, archive));

        Assert.Equal(XivVaultErrorKind.GameRunning, error.Kind);
        Assert.False(File.Exists(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
        AssertNoWorkFolderLeft(host);
    }

    [Fact]
    public async Task Cancelling_during_downloads_changes_nothing()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        host.Downloads.BeforeRequest = url =>
        {
            if (url.Contains("/Plugin/Download/", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true }, cancellationToken: cancel.Token));

        Assert.False(File.Exists(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
        AssertNoWorkFolderLeft(host);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plugins_are_never_written_through_a_link(bool linkedPluginsFolder)
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        var elsewhere = Path.Combine(host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        if (linkedPluginsFolder)
        {
            RestoreHardeningTests.CreateJunction(InstalledPlugins(root), elsewhere);
        }
        else
        {
            Directory.CreateDirectory(InstalledPlugins(root));
            RestoreHardeningTests.CreateJunction(Path.Combine(InstalledPlugins(root), Wotsit.Name), elsewhere);
        }

        var result = await RestoreAsync(host, archive);

        var report = Report(result, chosen: 2);
        Assert.Equal(PluginInstallOutcome.LinkedFolder, OutcomeOf(report, Wotsit));
        string[] installed = linkedPluginsFolder ? [] : [Orchestrion.Name];
        Assert.Equal(installed, report.Installed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task The_dalamudUserData_layout_installs_into_the_root_s_plugins_folder()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        var userData = Path.Combine(root, "dalamudUserData");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "dalamudConfig.json"), "{}");
        var link = Repository(host).Add(Wotsit.Name);

        var result = await RestoreAsync(host, archive);

        Assert.Equal([Wotsit.Name], Report(result, chosen: 1).Installed);
        AssertInstalled(root, Wotsit, host.Downloads.Bodies[link], "OFFICIAL");
        Assert.False(Directory.Exists(Path.Combine(userData, "installedPlugins")));
        Assert.Equal(userData, result.TargetDataPath);
    }

    [Theory]
    [InlineData("../Escaped")]
    [InlineData("..\\Escaped")]
    [InlineData("Nested/../../Escaped")]
    [InlineData("C:Escaped")]
    public async Task A_repository_name_that_leaves_the_plugins_folder_is_refused(string name)
    {
        using var host = new TestHost();
        var hostile = (Name: name, Id: Guid.Parse("7d61efb4-60e2-4c9f-9f90-81d26fc07e07"));
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([hostile, Wotsit], repositories: [(Ticked, true)]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        var link = repository.Add(name, repository: Ticked);

        var result = await RestoreAsync(host, archive, Ticked);

        var report = Report(result, chosen: 2);
        Assert.Equal(PluginInstallOutcome.PackageRefused, OutcomeOf(report, hostile));
        Assert.Equal([Wotsit.Name], report.Installed);
        Assert.DoesNotContain(link, host.Downloads.Requests);
        Assert.Equal([Wotsit.Name], Directory.GetDirectories(InstalledPlugins(root)).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFileSystemEntries(host.Environment.Root, "*Escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_in_the_way_fails_that_plugin_and_deletes_nothing()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        Directory.CreateDirectory(InstalledPlugins(root));
        var inTheWay = Path.Combine(InstalledPlugins(root), Wotsit.Name);
        File.WriteAllText(inTheWay, "someone else's file");

        var result = await RestoreAsync(host, archive);

        var report = Report(result, chosen: 2);
        Assert.Equal(PluginInstallOutcome.WriteFailed, OutcomeOf(report, Wotsit));
        Assert.Equal([Orchestrion.Name], report.Installed);
        Assert.Equal("someone else's file", File.ReadAllText(inTheWay));
        Assert.Empty(Directory.GetFiles(root, "*.xiv-vault-restore", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Logs_name_no_plugin_repository_or_plugin_ID()
    {
        using var host = new TestHost(configure: services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<ILoggerProvider>(provider =>
                new FileLoggerProvider(Path.Combine(provider.GetRequiredService<IAppEnvironment>().DataDirectory, "logs"), LogLevel.Trace));
        });
        var plugins = new[] { Wotsit, Orchestrion, Moodles, Lifestream, Tippy };
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig(plugins, repositories: [(Ticked, true), (Unticked, true)]));
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        host.Downloads.Bodies.Remove(repository.Add(Orchestrion.Name));
        repository.Add(Moodles.Name, repository: Ticked);
        repository.Add(Lifestream.Name, package: FakePluginRepository.Package(Lifestream.Name, version: "9.9.9.9"));
        repository.Add(Tippy.Name, repository: Unticked);

        var result = await RestoreAsync(host, archive, Ticked);

        Assert.Equal(2, Report(result, chosen: 5).Installed.Count);
        var text = string.Concat(Directory.GetFiles(Path.Combine(host.Environment.DataDirectory, "logs")).Select(File.ReadAllText));
        Assert.Contains("Restored", text, StringComparison.Ordinal);
        var secrets = plugins.SelectMany(plugin => new[] { plugin.Name, plugin.Id.ToString(), plugin.Id.ToString("N") })
            .Concat(host.Downloads.Bodies.Keys)
            .Concat(["raw.example.com", "other.example.org", "kamori.goats.dev", "example"]);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Preview_lists_the_missing_plugins_and_the_HTTPS_repositories_by_host()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig(
            [Wotsit, Orchestrion, Moodles],
            repositories: [(Ticked, true), ("https://plügins.example.com/repo.json", true), ("http://insecure.example.com/repo.json", true), (Disabled, false)]));
        var root = NewPc(host);
        InstallLocally(root, Orchestrion);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);

        var plugins = Assert.IsType<PluginInstallPreview>(preview.Plugins);
        Assert.Equal([Moodles.Name, Wotsit.Name], plugins.Missing.Order(StringComparer.Ordinal));
        Assert.Equal(
            [new CustomRepository(Ticked, "raw.example.com"), new CustomRepository("https://plügins.example.com/repo.json", "xn--plgins-4ya.example.com")],
            plugins.CustomRepositories);
        Assert.True(plugins.DalamudReady);
        Assert.Empty(host.Downloads.Requests);
    }

    [Fact]
    public async Task Preview_says_when_Dalamud_is_not_ready()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        NewPc(host, dalamudReady: false);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);

        Assert.False(preview.Plugins!.DalamudReady);
        Assert.Equal([Wotsit.Name], preview.Plugins.Missing);
    }

    [Fact]
    public async Task Preview_has_no_plugins_without_Dalamud_settings_or_an_XIVLauncher_folder()
    {
        using var host = new TestHost();
        var settingsOnly = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var noDalamudSettings = new ArchiveBuilder()
            .File("payload/pluginConfigs/Wotsit.json", "{}")
            .Save(Path.Combine(host.BackupFolder, "xiv-vault-2026-09-28-183800.zip"));

        var withoutLauncher = await host.Restores.PreviewAsync(settingsOnly, cancellationToken: Ct);
        NewPc(host);
        var withoutSettings = await host.Restores.PreviewAsync(noDalamudSettings, cancellationToken: Ct);

        Assert.Null(withoutLauncher.Target);
        Assert.Null(withoutLauncher.Plugins);
        Assert.Null(withoutSettings.Plugins);
        Assert.NotNull((await host.Restores.PreviewAsync(settingsOnly, cancellationToken: Ct)).Plugins);
    }

    /// <summary>Writes half of one URL's body, then fails the way a dropped connection does.</summary>
    private sealed class InterruptedDownloads(FakeWebDownloader inner) : IWebDownloader
    {
        public string? Url { get; set; }

        public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
        {
            if (url != Url)
            {
                inner.Download(url, accept, destination, maxBytes, cancellationToken);
                return;
            }

            var body = inner.Bodies[url];
            destination.Write(body, 0, body.Length / 2);
            throw new WebDownloadException("the download was interrupted");
        }
    }
}
