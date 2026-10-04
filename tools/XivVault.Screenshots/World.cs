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

    private World(string root, bool withHistory)
    {
        Root = root;
        Environment = new FakeEnvironment(root);
        Clock = new FakeClock(Local(2026, 9, 14, 12, 0));
        var collection = new ServiceCollection();
        collection.AddSingleton<IAppEnvironment>(Environment);
        collection.AddSingleton<TimeProvider>(Clock);
        collection.AddSingleton<IProcessInspector, NoProcesses>();
        collection.AddSingleton<ICommandRunner, FakeScheduler>();
        collection.AddSingleton<IMotionSettings, StillMotion>();
        collection.AddSingleton<IShellService, NoShell>();
        collection.AddXivVaultDesktop();
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

    public IServiceProvider Services => _services;

    public string LauncherPath => Path.Combine(Environment.RoamingAppData, "XIVLauncher");

    public static World Create(string root, bool withHistory) => new(root, withHistory);

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

        // Random bytes don't compress, so archive sizes look like a real configuration's.
        File.WriteAllBytes(Path.Combine(LauncherPath, "dalamudVfs.db"), RandomNumberGenerator.GetBytes(17_800_000));
        File.WriteAllText(Path.Combine(LauncherPath, "dalamudUI.ini"), "[Window][Debug##Default]\nPos=60,60\n");
        Directory.CreateDirectory(Path.Combine(LauncherPath, "installedPlugins"));
        for (var i = 0; i < pluginCount; i++)
        {
            AddPlugin(Plugins[i]);
        }

        var install = Path.Combine(Environment.LocalAppData, "XIVLauncher");
        Directory.CreateDirectory(Path.Combine(install, "app-1.1.2"));
        File.WriteAllText(Path.Combine(install, "XIVLauncher.exe"), "stub");
    }

    private void AddPlugin(string name)
    {
        var folder = Path.Combine(LauncherPath, "pluginConfigs");
        File.WriteAllText(Path.Combine(folder, name + ".json"), $$"""{ "Version": 3, "Plugin": "{{name}}" }""");
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
