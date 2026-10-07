using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Core.State;
using XivVault.Desktop;
using XivVault.Desktop.Services;

namespace XivVault.Screenshots;

/// <summary>
/// A believable PC: an XIVLauncher folder with 43 plugin configs and a backup history like the
/// mockup's (scheduled, manual and a pre-restore snapshot over two weeks).
/// </summary>
internal sealed class World : IDisposable
{
    public static readonly string[] Plugins =
    [
        "Artisan", "AutoRetainer", "AutoHook", "Yes Already", "Pandora's Box", "Splatoon", "Pixel Perfect", "Waymark Preset Plugin",
        "SimpleTweaksPlugin", "ChatTwo", "Dalamud.FindAnything", "DelvUI", "Lifestream", "MacroChain", "MapPartyAssist", "MarketBoardPlugin",
        "Moodles", "NoTankYou", "Orchestrion", "PartyFinderReborn", "PingPlugin", "QoLBar", "ReadyCheckHelper", "RotationSolver",
        "SamplePlugin", "SettingsEditor", "ShowCombo", "SonarPlugin", "Teleporter", "TextAdvance", "TimelineHelper", "Tippy",
        "Wotsit", "XIVCombo", "XivAlexander", "BetterPartyFinder", "CraftingList", "DailyDuty", "GatherBuddy", "GoodMemory",
        "HUDManager", "InventoryTools", "KamiToolKit",
    ];

    private readonly ServiceProvider _services;

    private World(string root, bool withHistory, bool demo)
    {
        Root = root;
        Environment = new FakeEnvironment(root);
        Clock = new FakeClock(Local(2026, 9, 14, 12, 0));
        var collection = new ServiceCollection();
        collection.AddSingleton<IAppEnvironment>(Environment);
        collection.AddSingleton<TimeProvider>(Clock);
        collection.AddSingleton<IProcessInspector, NoProcesses>();
        collection.AddSingleton<ICommandRunner, FakeScheduler>();
        collection.AddSingleton<IFileAvailability>(Cloud);
        collection.AddXivVaultDesktop();

        // The desktop registers its own UI services, so these replace them afterwards.
        collection.AddSingleton<IMotionSettings>(new FixedMotion(reduce: !demo));
        collection.AddSingleton<IShellService, NoShell>();
        collection.AddSingleton<IAppUpdater>(Updater);
        if (demo)
        {
            collection.AddSingleton<IBackupService>(provider => new PacedBackupService(provider.GetRequiredService<BackupService>(), TimeSpan.FromSeconds(2.5)));
            collection.AddSingleton<IRestoreService>(provider => new PacedRestoreService(ActivatorUtilities.CreateInstance<RestoreService>(provider), TimeSpan.FromSeconds(1.5)));
        }

        _services = collection.BuildServiceProvider();
        CreateLauncher(withHistory ? 40 : 43);
        if (withHistory)
        {
            CreateHistory();
        }

        Clock.Now = Local(2026, 9, 28, 15, 52);
    }

    public string Root { get; }

    public FakeEnvironment Environment { get; }

    public FakeClock Clock { get; }

    public FakeUpdater Updater { get; } = new();

    public CloudFiles Cloud { get; } = new();

    public IServiceProvider Services => _services;

    public string LauncherPath => Path.Combine(Environment.RoamingAppData, "XIVLauncher");

    /// <summary>A fake PC. A demo world has animations on and paces backups and restores for recording.</summary>
    public static World Create(string root, bool withHistory, bool demo = false) => new(root, withHistory, demo);

    /// <summary>Leaves every archive in the cloud and forgets their verification, as on a new PC.</summary>
    public void MoveBackupsToCloud()
    {
        var destination = _services.GetRequiredService<IConfigStore>().Load().BackupDestination!;
        Cloud.OnlineOnly.UnionWith(Directory.EnumerateFiles(destination, "*.zip").Select(Path.GetFullPath));
        _services.GetRequiredService<IStateStore>().Update(state =>
        {
            state.Verifications.Clear();
            return state;
        });
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private void CreateLauncher(int pluginCount)
    {
        Directory.CreateDirectory(Path.Combine(LauncherPath, "pluginConfigs"));
        File.WriteAllText(Path.Combine(LauncherPath, "launcherConfigV3.json"), "{}");
        File.WriteAllText(Path.Combine(LauncherPath, "dalamudConfig.json"),
            """{ "ThirdRepoList": { "$values": [ { "Url": "https://repo.example.com/a.json" }, { "Url": "https://repo.example.com/b.json" } ] } }""");

        File.WriteAllBytes(Path.Combine(LauncherPath, "dalamudVfs.db"), RandomNumberGenerator.GetBytes(900_000));
        File.WriteAllText(Path.Combine(LauncherPath, "dalamudUI.ini"), "[Window][Debug##Default]\nPos=60,60\n");
        Directory.CreateDirectory(Path.Combine(LauncherPath, "installedPlugins"));
        for (var i = 0; i < pluginCount; i++)
        {
            AddPlugin(Plugins[i]);
        }

        // XIVLauncher 7 installs with Velopack: the app and its version in current\.
        var current = Path.Combine(Environment.LocalAppData, "XIVLauncher", "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "XIVLauncher.exe"), "stub");
        File.WriteAllText(Path.Combine(current, "sq.version"), "<package><metadata><id>XIVLauncher</id><version>7.0.20</version></metadata></package>");
    }

    private void AddPlugin(string name)
    {
        var folder = Path.Combine(LauncherPath, "pluginConfigs");
        File.WriteAllText(Path.Combine(folder, name + ".json"), $$"""{ "Version": 3, "Plugin": "{{name}}" }""");

        // Most of a real configuration's size is in plugin data, spread unevenly across plugins.
        // Random bytes don't compress, so archive sizes and backup progress look like the real thing.
        var size = 150_000 + (name.Sum(c => c) * 7919 % 500) * 1_000;
        Directory.CreateDirectory(Path.Combine(folder, name));
        File.WriteAllBytes(Path.Combine(folder, name, "profiles.dat"), RandomNumberGenerator.GetBytes(size));
    }

    private void CreateHistory()
    {
        var backups = _services.GetRequiredService<IBackupService>();
        var restores = _services.GetRequiredService<IRestoreService>();
        void At(DateTimeOffset when) => Clock.Now = when;
        BackupResult Back(BackupKind kind) => backups.CreateBackupAsync(new BackupRequest(kind)).GetAwaiter().GetResult();

        At(Local(2026, 9, 14, 12, 0));
        Back(BackupKind.Scheduled);
        AddPlugin(Plugins[40]);
        At(Local(2026, 9, 19, 12, 0));
        Back(BackupKind.Scheduled);
        At(Local(2026, 9, 21, 16, 32));
        Back(BackupKind.Manual);
        AddPlugin(Plugins[41]);
        At(Local(2026, 9, 24, 12, 0));
        var sep24 = Back(BackupKind.Scheduled);
        At(Local(2026, 9, 25, 18, 47));
        restores.RestoreAsync(new RestoreRequest(sep24.Record.FilePath)).GetAwaiter().GetResult();
        At(Local(2026, 9, 26, 12, 0));
        Back(BackupKind.Scheduled);
        AddPlugin(Plugins[42]);
        At(Local(2026, 9, 27, 21, 14));
        Back(BackupKind.Manual);
        At(Local(2026, 9, 28, 13, 38));
        Back(BackupKind.Scheduled);
        _services.GetRequiredService<IStateStore>().Update(state => state with
        {
            LastScheduledRun = new ScheduledRunRecord(Local(2026, 9, 28, 13, 38).UtcDateTime, ScheduledRunResult.Success, "Successful", null),
        });
        _services.GetRequiredService<IConfigStore>().Save(_services.GetRequiredService<IConfigStore>().Load() with
        {
            Schedule = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Sunday], Time = "12:00" },
        });

        // Everything on disk was "last touched" before the latest backup, except two plugins
        // configured since, so the restore review has something to warn about.
        foreach (var file in Directory.EnumerateFiles(LauncherPath, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTime(file, new DateTime(2026, 9, 27, 20, 0, 0));
        }

        foreach (var plugin in new[] { "Splatoon", "Pixel Perfect" })
        {
            File.SetLastWriteTime(Path.Combine(LauncherPath, "pluginConfigs", plugin + ".json"), new DateTime(2026, 9, 28, 14, 30, 0));
        }
    }
}
