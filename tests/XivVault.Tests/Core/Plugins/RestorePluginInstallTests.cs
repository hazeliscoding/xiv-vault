using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
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

    internal static readonly (string Name, Guid Id) Wotsit = ("Wotsit", Guid.Parse("1d0b8f5e-0a8c-4c39-9f3a-2b7c0f6a1e01"));
    internal static readonly (string Name, Guid Id) Orchestrion = ("Orchestrion", Guid.Parse("2e1c9a6f-1b9d-4d4a-8a4b-3c8d1a7b2f02"));
    private static readonly (string Name, Guid Id) SimpleTweaks = ("SimpleTweaksPlugin", Guid.Parse("3f2dab70-2cae-4e5b-9b5c-4d9e2b8c3a03"));
    internal static readonly (string Name, Guid Id) Moodles = ("Moodles", Guid.Parse("4a3ebc81-3dbf-4f6c-8c6d-5eaf3c9d4b04"));
    internal static readonly (string Name, Guid Id) Lifestream = ("Lifestream", Guid.Parse("5b4fcd92-4ec0-4a7d-9d7e-6fb04dae5c05"));
    internal static readonly (string Name, Guid Id) Tippy = ("Tippy", Guid.Parse("6c50dea3-5fd1-4b8e-8e8f-70c15ebf6d06"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static FakePluginRepository Repository(TestHost host) => new(host.Downloads, Path.Combine(host.Environment.Root, "web"));

    /// <summary>Backs up another PC whose Dalamud settings are <paramref name="config"/>.</summary>
    internal static Task<string> BackUpOldPcAsync(TestHost host, string config, IEnumerable<string>? pluginConfigs = null) =>
        BackUpOldPcAsync(host, Encoding.UTF8.GetBytes(config), pluginConfigs);

    private static async Task<string> BackUpOldPcAsync(TestHost host, byte[] config, IEnumerable<string>? pluginConfigs = null)
    {
        var oldPc = FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"), plugins: pluginConfigs);
        File.WriteAllBytes(Path.Combine(oldPc.DataPath, "dalamudConfig.json"), config);
        var backup = await host.Backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root }, cancellationToken: Ct);
        return backup.Record.FilePath;
    }

    /// <summary>The text as UTF-8, with each # replaced by a byte UTF-8 never uses.</summary>
    private static byte[] NotUtf8(string text) => [.. Encoding.UTF8.GetBytes(text).Select(value => value == (byte)'#' ? (byte)0xFF : value)];

    /// <summary>A new PC where XIVLauncher has run once and, unless told otherwise, has finished downloading Dalamud.</summary>
    internal static string NewPc(TestHost host, bool dalamudReady = true, string folder = "XIVLauncher")
    {
        var root = Path.Combine(host.Environment.RoamingAppData, folder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "launcherConfigV3.json"), "{}");
        if (dalamudReady)
        {
            FakeDalamud.InstallDalamud(root, Dalamud);
        }

        return root;
    }

    /// <summary>A plugin Dalamud would load: its DLL and a manifest with its ID.</summary>
    internal static void InstallLocally(string root, (string Name, Guid Id) plugin)
    {
        var folder = Path.Combine(root, "installedPlugins", plugin.Name, "0.9.0.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, plugin.Name + ".dll"), "binary");
        File.WriteAllText(Path.Combine(folder, plugin.Name + ".json"), $$"""{ "InternalName": "{{plugin.Name}}", "WorkingPluginId": "{{plugin.Id}}" }""");
    }

    private static Task<RestoreResult> RestoreAsync(TestHost host, string archive, params string[] customRepositoryHosts) =>
        host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true, CustomRepositoryHosts = customRepositoryHosts }, cancellationToken: Ct);

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

    private static void AssertNoSafetySnapshot(TestHost host) =>
        Assert.DoesNotContain(host.Catalog.List(host.BackupFolder), record => record.IsSafetySnapshot);

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
        Assert.Empty(preview.PluginsToInstall(RestoreSelection.Everything));
        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
        Assert.False(Directory.Exists(InstalledPlugins(root)));
        Assert.True(result.DalamudConfigRestored);
    }

    [Fact]
    public void Only_a_prerelease_version_is_a_beta()
    {
        Assert.True(XivVaultInfo.IsPrerelease("1.1.0-beta.1"));
        Assert.False(XivVaultInfo.IsPrerelease("1.1.0"));
    }

    [Fact]
    public void Installs_follow_this_version_when_a_front_end_registers_nothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebDownloader>(new FakeWebDownloader());
        services.AddXivVaultCore();
        using var provider = services.BuildServiceProvider();

        // Checked against the version string rather than IsBeta: tests build as a stable version, where installs must be off.
        Assert.Equal(XivVaultInfo.IsPrerelease(XivVaultInfo.Version), provider.GetRequiredService<PluginInstalls>().Available);
    }

    [Fact]
    public async Task XIVLauncherCN_gets_no_plugin_installs()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host, folder: "XIVLauncherCN");
        Repository(host).Add(Wotsit.Name);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive);

        Assert.Equal(root, preview.Target!.RootPath);
        Assert.Null(preview.Plugins);
        Assert.Null(result.Plugins);
        Assert.Empty(host.Downloads.Requests);
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
    public async Task Plugins_without_settings_in_the_backup_install_when_another_choice_is_left_out()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]), pluginConfigs: [Wotsit.Name]);
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);

        // Every plugin configuration ticked, and something else left out, as the app does for system settings on a new PC.
        // Orchestrion has no settings in the backup, so the review never offered it to leave out.
        var result = await host.Restores.RestoreAsync(
            new RestoreRequest(archive) { InstallPlugins = true, Selection = RestoreSelection.Only(preview.Backup.PluginNames, dalamudSettings: true) },
            cancellationToken: Ct);

        Assert.Equal([Orchestrion.Name, Wotsit.Name], Report(result, chosen: 2).Installed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_plugin_installs_with_the_Dalamud_settings_unless_its_own_settings_are_left_out()
    {
        string[] inBackup = [Wotsit.Name, Orchestrion.Name];
        var wotsitOnly = RestoreSelection.Only(["wotsit"], dalamudSettings: true);

        Assert.True(RestoreSelection.Everything.InstallsPlugin(Orchestrion.Name, inBackup));
        Assert.True(wotsitOnly.InstallsPlugin(Wotsit.Name, inBackup));
        Assert.False(wotsitOnly.InstallsPlugin("orchestrion", inBackup));

        // The backup holds no settings for Moodles, so the review never offered it to leave out.
        Assert.True(wotsitOnly.InstallsPlugin(Moodles.Name, inBackup));
        Assert.False(RestoreSelection.Only([Wotsit.Name], dalamudSettings: false).InstallsPlugin(Wotsit.Name, inBackup));
    }

    public static TheoryData<string[]?, bool, string[]> Selections => new()
    {
        { null, true, [Moodles.Name, Orchestrion.Name, Wotsit.Name] },
        { ["wotsit"], true, [Moodles.Name, Wotsit.Name] },
        { [], true, [Moodles.Name] },
        { [Wotsit.Name, Orchestrion.Name], false, [] },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task The_preview_names_the_plugins_a_restore_installs(string[]? plugins, bool dalamudSettings, string[] expected)
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles, Tippy]), pluginConfigs: [Wotsit.Name, Orchestrion.Name]);
        InstallLocally(NewPc(host), Tippy);
        var repository = Repository(host);
        foreach (var plugin in new[] { Wotsit, Orchestrion, Moodles, Tippy })
        {
            repository.Add(plugin.Name);
        }

        var selection = plugins is null ? RestoreSelection.Everything : RestoreSelection.Only(plugins, dalamudSettings);
        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true, Selection = selection }, cancellationToken: Ct);

        Assert.Equal(expected, preview.PluginsToInstall(selection).Order(StringComparer.Ordinal));
        Assert.Equal(expected, (result.Plugins?.Installed ?? []).Order(StringComparer.Ordinal));
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

        var result = await RestoreAsync(host, archive, "RAW.example.COM", "disabled.example.net", "notenabled.example.com");

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
    public async Task A_ticked_host_installs_from_every_repository_the_settings_turn_on_there()
    {
        using var host = new TestHost();
        const string sameHost = "https://raw.example.com/Another/repo.json";
        const string offAtSameHost = "https://raw.example.com/Old/repo.json";
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig(
            [Moodles, Lifestream, Tippy],
            repositories: [(Ticked, true), (sameHost, true), (offAtSameHost, false)]));
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Moodles.Name, repository: Ticked);
        repository.Add(Lifestream.Name, repository: sameHost);
        repository.Add(Tippy.Name, repository: offAtSameHost);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive, "raw.example.com");

        Assert.Equal([new CustomRepositoryHost("raw.example.com", 2)], preview.Plugins!.CustomRepositories);
        var report = Report(result, chosen: 3);
        Assert.Equal([Lifestream.Name, Moodles.Name], report.Installed.Order(StringComparer.Ordinal));
        Assert.Equal(PluginInstallOutcome.NotOffered, OutcomeOf(report, Tippy));
        Assert.DoesNotContain(offAtSameHost, host.Downloads.Requests);
    }

    [Fact]
    public async Task A_repository_over_plain_HTTP_is_never_requested()
    {
        using var host = new TestHost();
        const string insecure = "http://insecure.example.com/repo.json";
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Tippy], repositories: [(insecure, true)]));
        NewPc(host);
        Repository(host).Add(Tippy.Name, repository: insecure);

        var result = await RestoreAsync(host, archive, "insecure.example.com");

        Assert.DoesNotContain(insecure, host.Downloads.Requests);
        Assert.Equal(PluginInstallOutcome.NotOffered, OutcomeOf(Report(result, chosen: 1), Tippy));
    }

    [Fact]
    public async Task A_repository_whose_host_has_no_punycode_form_is_never_offered_or_requested()
    {
        using var host = new TestHost();

        // Dalamud saves any address Uri accepts, and Uri accepts a soft hyphen pasted into a host but can't convert it.
        const string unconvertible = "https://­.example.com/repo.json";
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Moodles], repositories: [(unconvertible, true), (Ticked, true)]));
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: unconvertible);
        host.Downloads.Bodies[Ticked] = "[]"u8.ToArray();

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive, "\u00AD.example.com", "raw.example.com");

        Assert.Equal([new CustomRepositoryHost("raw.example.com", 1)], preview.Plugins!.CustomRepositories);
        Assert.DoesNotContain(unconvertible, host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.Equal([Wotsit.Name], report.Installed);
        Assert.Equal(PluginInstallOutcome.NotOffered, OutcomeOf(report, Moodles));
    }

    [Fact]
    public async Task A_ticked_list_that_cannot_be_read_leaves_its_plugins_unavailable()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Moodles], repositories: [(Ticked, true)]));
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Moodles.Name, repository: Ticked);
        host.Downloads.Bodies.Remove(Ticked);

        var result = await RestoreAsync(host, archive, "raw.example.com");

        var report = Report(result, chosen: 2);
        Assert.Equal([Wotsit.Name], report.Installed);
        Assert.Equal(PluginInstallOutcome.RepositoryUnavailable, OutcomeOf(report, Moodles));
    }

    [Fact]
    public async Task Lists_and_plugins_are_requested_as_Dalamud_requests_them()
    {
        var web = new FakeWebDownloader();
        var recording = new RecordingDownloads(web);
        using var host = new TestHost(configure: services => services.AddSingleton<IWebDownloader>(recording));
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles], repositories: [(Ticked, true)]));
        NewPc(host);
        var repository = new FakePluginRepository(web, Path.Combine(host.Environment.Root, "web"));
        string[] links = [repository.Add(Wotsit.Name), repository.Add(Orchestrion.Name), repository.Add(Moodles.Name, repository: Ticked)];

        var result = await RestoreAsync(host, archive, "raw.example.com");

        Assert.Equal(3, Report(result, chosen: 3).Installed.Count);
        List<(string Url, string Accept, long MaxBytes)> expected =
        [
            (FakePluginRepository.Official, "application/json", 32L << 20),
            (Ticked, "application/json", 32L << 20),
            .. links.Select(link => (link, "application/zip", 512L << 20)),
        ];
        Assert.Equal(expected, recording.Requests.Select(request => (request.Url, request.Accept, request.MaxBytes)));

        // Each zip is deleted once it is unpacked, before the next one downloads.
        Assert.DoesNotContain(recording.Requests, request => request.EarlierZipLeft);
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

        var result = await RestoreAsync(host, archive, "raw.example.com");

        Assert.Equal([FakePluginRepository.Official], host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.RepositoryUnavailable, plugin.Outcome));
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task An_official_list_that_cannot_be_read_installs_nothing()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        host.Downloads.Bodies[FakePluginRepository.Official] = "<html>Service unavailable</html>"u8.ToArray();

        var result = await RestoreAsync(host, archive);

        Assert.Equal([FakePluginRepository.Official], host.Downloads.Requests);
        var report = Report(result, chosen: 2);
        Assert.Equal(2, report.NotInstalled.Count);
        Assert.All(report.NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.RepositoryUnavailable, plugin.Outcome));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task Failed_and_refused_downloads_leave_nothing_behind()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion, Moodles, Lifestream, Tippy]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        host.Downloads.Bodies.Remove(repository.Add(Orchestrion.Name));
        var moodles = repository.Add(Moodles.Name);
        host.Downloads.Interruptions[moodles] = host.Downloads.Bodies[moodles].Length / 2;
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
        AssertNoSafetySnapshot(host);
    }

    [Fact]
    public async Task Settings_saved_while_plugins_download_are_in_the_safety_snapshot()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        var current = Path.Combine(root, "dalamudConfig.json");
        File.WriteAllText(current, "before the downloads");
        Repository(host).Add(Wotsit.Name);
        host.Downloads.BeforeRequest = url =>
        {
            // The game started and closed again while the plugin downloaded, and Dalamud saved its settings.
            if (url.Contains("/Plugin/Download/", StringComparison.Ordinal))
            {
                File.WriteAllText(current, "saved during the downloads");
            }
        };

        var result = await RestoreAsync(host, archive);

        Assert.Equal(config, File.ReadAllText(current));
        using var snapshot = ZipFile.OpenRead(result.SafetySnapshot.FilePath);
        using var saved = new StreamReader(snapshot.GetEntry("payload/dalamudConfig.json")!.Open());
        Assert.Equal("saved during the downloads", saved.ReadToEnd());
    }

    [Fact]
    public async Task A_link_made_while_plugins_download_is_refused_before_anything_changes()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]), pluginConfigs: [Wotsit.Name]);
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);
        var elsewhere = Path.Combine(host.Environment.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        host.Downloads.BeforeRequest = url =>
        {
            if (url.Contains("/Plugin/Download/", StringComparison.Ordinal))
            {
                RestoreHardeningTests.CreateJunction(Path.Combine(root, "pluginConfigs"), elsewhere);
            }
        };

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, archive));

        Assert.Equal(XivVaultErrorKind.RestoreValidationFailed, error.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.False(File.Exists(Path.Combine(root, "dalamudConfig.json")));
        Assert.False(Directory.Exists(InstalledPlugins(root)));
        AssertNoWorkFolderLeft(host);
        AssertNoSafetySnapshot(host);
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
        AssertNoSafetySnapshot(host);
    }

    [Fact]
    public async Task Plugins_are_written_only_once_the_settings_are()
    {
        using var host = new TestHost(configure: services => services.AddSingleton<IRestoreService>(provider => new RestoreService(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IXivLauncherLocator>(),
            provider.GetRequiredService<ArchiveValidator>(),
            provider.GetRequiredService<BackupCatalog>(),
            provider.GetRequiredService<BackupService>(),
            provider.GetRequiredService<PortableStateScanner>(),
            provider.GetRequiredService<GameProcessGuard>(),
            provider.GetRequiredService<PathDisplay>(),
            provider.GetRequiredService<IAppEnvironment>(),
            provider.GetRequiredService<RetentionService>(),
            provider.GetRequiredService<OperationLock>(),
            NullLogger<RestoreService>.Instance,
            provider.GetRequiredService<PluginInstaller>(),
            provider.GetRequiredService<PluginInstalls>(),
            new FailingConfigWriter())));
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);

        var error = await Assert.ThrowsAsync<XivVaultException>(() => RestoreAsync(host, archive));

        Assert.Contains("put back", error.Message);
        Assert.False(Directory.Exists(InstalledPlugins(root)));
    }

    [Fact]
    public async Task Once_the_settings_are_restored_the_restore_completes_whatever_installing_throws()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        var installing = 0;
        var progress = new InlineProgress(value =>
        {
            // A front end failing to show the second install.
            if (value.Stage == RestoreStage.InstallingPlugins && ++installing == 2)
            {
                throw new InvalidOperationException("The window was closed.");
            }
        });

        var result = await host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true }, progress, Ct);

        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        var report = Report(result, chosen: 2);
        var installed = Assert.Single(report.Installed);
        Assert.Equal(PluginInstallOutcome.WriteFailed, Assert.Single(report.NotInstalled).Outcome);
        Assert.Equal([installed], Directory.GetDirectories(InstalledPlugins(root)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Progress_moves_on_through_the_downloads_and_never_goes_back()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]));
        NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        var reports = new List<RestoreProgress>();
        var atRequests = new List<RestoreProgress>();
        host.Downloads.BeforeRequest = _ => atRequests.Add(reports[^1]);

        await host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true }, new InlineProgress(reports.Add), Ct);

        // The plugin list, then each plugin, moves the bar on.
        Assert.Equal(3, atRequests.Count);
        Assert.All(atRequests, report => Assert.Equal(RestoreStage.DownloadingPlugins, report.Stage));
        Assert.True(atRequests[0].Percent < atRequests[1].Percent && atRequests[1].Percent < atRequests[2].Percent);
        Assert.Equal(reports.Select(report => report.Percent).Order(), reports.Select(report => report.Percent));

        // The safety snapshot is taken after the downloads, right before anything changes.
        Assert.Equal(
            [
                RestoreStage.VerifyingIntegrity, RestoreStage.Extracting, RestoreStage.DownloadingPlugins, RestoreStage.CreatingSafetySnapshot,
                RestoreStage.RestoringPluginConfigs, RestoreStage.RestoringDalamudSettings, RestoreStage.InstallingPlugins, RestoreStage.Completed,
            ],
            reports.Select(report => report.Stage).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_restore_that_downloads_nothing_moves_at_its_usual_pace(bool installPlugins)
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        InstallLocally(NewPc(host), Wotsit);
        var reports = new List<RestoreProgress>();

        await host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = installPlugins }, new InlineProgress(reports.Add), Ct);

        Assert.Equal(40, reports.First(report => report.Stage == RestoreStage.RestoringPluginConfigs).Percent);
        Assert.Equal(reports.Select(report => report.Percent).Order(), reports.Select(report => report.Percent));
        Assert.DoesNotContain(reports, report => report.Stage is RestoreStage.DownloadingPlugins or RestoreStage.InstallingPlugins);
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

        var result = await RestoreAsync(host, archive, "raw.example.com");

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

    // System.Text.Json parses text that isn't UTF-8, and fails only when a value is read; Newtonsoft, which Dalamud uses,
    // reads such a byte as a replacement character. Either way, the settings must still restore.
    [Fact]
    public async Task Settings_that_are_not_UTF8_never_stop_a_restore()
    {
        using var host = new TestHost();
        var config = NotUtf8(FakePluginRepository.DalamudConfig([("Wots#t", Wotsit.Id), Orchestrion]));
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        Repository(host).Add(Orchestrion.Name);

        await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive);

        Assert.True(result.DalamudConfigRestored);
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(root, "dalamudConfig.json")));
    }

    [Fact]
    public async Task An_installed_manifest_that_is_not_UTF8_never_stops_a_restore()
    {
        using var host = new TestHost();
        var config = FakePluginRepository.DalamudConfig([Wotsit, Orchestrion]);
        var archive = await BackUpOldPcAsync(host, config);
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        repository.Add(Orchestrion.Name);
        InstallLocally(root, Orchestrion);
        var manifest = Path.Combine(InstalledPlugins(root), Orchestrion.Name, "0.9.0.0", Orchestrion.Name + ".json");
        var unreadable = NotUtf8("""{ "InternalName": "Orchestrion", "WorkingPluginId": "#" }""");
        File.WriteAllBytes(manifest, unreadable);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);
        var result = await RestoreAsync(host, archive);

        Assert.NotNull(preview.Plugins);
        Assert.Equal(config, File.ReadAllText(Path.Combine(root, "dalamudConfig.json")));
        Assert.DoesNotContain(Orchestrion.Name, Report(result, chosen: 2).Installed);
        Assert.Equal(unreadable, File.ReadAllBytes(manifest));
    }

    [Fact]
    public async Task A_ban_list_that_is_not_UTF8_never_stops_a_restore()
    {
        using var host = new TestHost();
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig([Wotsit]));
        var root = NewPc(host);
        Repository(host).Add(Wotsit.Name);
        FakeDalamud.WriteBanList(root, "[]");
        File.WriteAllBytes(Path.Combine(root, "dalamudAssets", "444", "UIRes", "bannedplugin.json"), NotUtf8("""[ { "Name": "Banned#", "AssemblyVersion": null } ]"""));

        var result = await RestoreAsync(host, archive);

        Assert.Equal([Wotsit.Name], Report(result, chosen: 1).Installed);
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
        const string gone = "https://gone.example.com/repo.json";
        var plugins = new[] { Wotsit, Orchestrion, SimpleTweaks, Moodles, Lifestream, Tippy };
        var archive = await BackUpOldPcAsync(host, FakePluginRepository.DalamudConfig(plugins, repositories: [(Ticked, true), (Unticked, true), (gone, true)]));
        var root = NewPc(host);
        var repository = Repository(host);
        repository.Add(Wotsit.Name);
        host.Downloads.Bodies.Remove(repository.Add(Orchestrion.Name));
        repository.Add(SimpleTweaks.Name);
        repository.Add(Moodles.Name, repository: Ticked);
        repository.Add(Lifestream.Name, package: FakePluginRepository.Package(Lifestream.Name, version: "9.9.9.9"));
        repository.Add(Tippy.Name, repository: Unticked);

        // Errors that name a plugin: a file in the way, whose error holds its path, and a front end failing mid-install.
        Directory.CreateDirectory(InstalledPlugins(root));
        File.WriteAllText(Path.Combine(InstalledPlugins(root), SimpleTweaks.Name), "in the way");
        var installing = 0;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == RestoreStage.InstallingPlugins && ++installing == 3)
            {
                throw new InvalidOperationException($"{Moodles.Name} couldn't be shown.");
            }
        });

        var first = await host.Restores.RestoreAsync(new RestoreRequest(archive) { InstallPlugins = true, CustomRepositoryHosts = ["raw.example.com", "gone.example.com"] }, progress, Ct);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        host.Downloads.Bodies[FakePluginRepository.Official] = "not a plugin list"u8.ToArray();
        var second = await RestoreAsync(host, archive, "raw.example.com");

        Assert.Equal([Wotsit.Name], Report(first, chosen: 6).Installed);
        Assert.All(Report(second, chosen: 6).NotInstalled, plugin => Assert.Equal(PluginInstallOutcome.RepositoryUnavailable, plugin.Outcome));
        var text = string.Concat(Directory.GetFiles(Path.Combine(host.Environment.DataDirectory, "logs")).Select(File.ReadAllText));
        foreach (var reason in new[] { "Restored", "couldn't be written", "stopped unexpectedly", "custom plugin list(s) couldn't be read", "official plugin list couldn't be read" })
        {
            Assert.Contains(reason, text, StringComparison.Ordinal);
        }

        var secrets = plugins.SelectMany(plugin => new[] { plugin.Name, plugin.Id.ToString(), plugin.Id.ToString("N") })
            .Concat(host.Downloads.Bodies.Keys)
            .Concat(["raw.example.com", "other.example.org", "gone.example.com", "kamori.goats.dev", "example"]);
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
            repositories:
            [
                (Ticked, true), ("https://plügins.example.com/repo.json", true), ("https://RAW.EXAMPLE.COM/Other/repo.json", true),
                ("http://insecure.example.com/repo.json", true), (Disabled, false),
            ]));
        var root = NewPc(host);
        InstallLocally(root, Orchestrion);

        var preview = await host.Restores.PreviewAsync(archive, cancellationToken: Ct);

        var plugins = Assert.IsType<PluginInstallPreview>(preview.Plugins);
        Assert.Equal([Moodles.Name, Wotsit.Name], plugins.Missing.Order(StringComparer.Ordinal));
        Assert.Equal(
            [new CustomRepositoryHost("raw.example.com", 2), new CustomRepositoryHost("xn--plgins-4ya.example.com", 1)],
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

    /// <summary>Records how each request is made, and whether an earlier plugin's zip was still in the work folder then.</summary>
    private sealed class RecordingDownloads(FakeWebDownloader inner) : IWebDownloader
    {
        private readonly List<string> _zips = [];

        public List<(string Url, string Accept, long MaxBytes, bool EarlierZipLeft)> Requests { get; } = [];

        public void Download(string url, string accept, Stream destination, long maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add((url, accept, maxBytes, _zips.Any(File.Exists)));
            if (destination is FileStream file)
            {
                _zips.Add(file.Name);
            }

            inner.Download(url, accept, destination, maxBytes, cancellationToken);
        }
    }

    /// <summary>Writes every file but dalamudConfig.json, which fails as a full disk would.</summary>
    private sealed class FailingConfigWriter : IRestoreFileWriter
    {
        private readonly RenameRestoreFileWriter _inner = new();

        public void Replace(string stagedPath, string targetPath)
        {
            if (Path.GetFileName(targetPath) == "dalamudConfig.json")
            {
                throw new IOException("There is not enough space on the disk.");
            }

            _inner.Replace(stagedPath, targetPath);
        }
    }

    private sealed class InlineProgress(Action<RestoreProgress> report) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => report(value);
    }
}
