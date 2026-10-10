using XivVault.Core.Backup;
using XivVault.Core.Discovery;

namespace XivVault.Core.Restore;

public sealed record RestoreRequest(string ArchivePath)
{
    /// <summary>Overrides the configured XIVLauncher folder for this restore.</summary>
    public string? Source { get; init; }

    public RestoreSelection Selection { get; init; } = RestoreSelection.Everything;
}

/// <summary>
/// What a restore writes: some or all of the backup's plugins, Dalamud settings as one choice, and
/// the game's settings with <c>FFXIV.cfg</c> as a choice of its own. The safety snapshot and
/// validation always cover everything, whatever is chosen.
/// </summary>
public sealed class RestoreSelection
{
    private RestoreSelection(IReadOnlySet<string>? plugins, bool dalamudSettings, bool gameSettings, bool gameConfig)
    {
        Plugins = plugins;
        DalamudSettings = dalamudSettings;
        GameSettings = gameSettings;
        GameConfig = gameConfig;
    }

    public static RestoreSelection Everything { get; } = new(null, dalamudSettings: true, gameSettings: true, gameConfig: true);

    /// <summary>The plugins to restore, by name; null means every plugin in the backup.</summary>
    public IReadOnlySet<string>? Plugins { get; }

    /// <summary><c>dalamudConfig.json</c>, <c>dalamudVfs.db</c> and <c>dalamudUI.ini</c>.</summary>
    public bool DalamudSettings { get; }

    /// <summary>Shared macros, appearance saves and every character's settings.</summary>
    public bool GameSettings { get; }

    /// <summary><c>FFXIV.cfg</c>, which holds resolution and monitor settings that may not suit another PC.</summary>
    public bool GameConfig { get; }

    /// <summary>Plugin names are matched without regard to case, as Windows matches file names.</summary>
    public static RestoreSelection Only(IEnumerable<string> plugins, bool dalamudSettings, bool gameSettings = false, bool gameConfig = false) =>
        new(new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase), dalamudSettings, gameSettings, gameConfig);

    public bool IsEverything => Plugins is null && DalamudSettings && GameSettings && GameConfig;

    public bool IncludesPlugin(string name) => Plugins is null || Plugins.Contains(name);

    /// <summary>
    /// Why this choice can't be restored from a backup holding <paramref name="backupPlugins"/>, or
    /// null when it can. Core, the command line and the app all ask here, so they agree.
    /// </summary>
    public string? ProblemIn(
        IReadOnlyCollection<string> backupPlugins,
        bool backupHasDalamudSettings,
        bool backupHasGameSettings = false,
        bool backupHasGameConfig = false)
    {
        var missing = PluginsMissingFrom(backupPlugins);
        if (missing.Count > 0)
        {
            return $"This backup has no settings for {Formatting.JoinWords(missing)}.";
        }

        // A full restore of a backup that holds nothing, such as the safety backup of a PC where
        // Dalamud never ran, is allowed: it writes nothing.
        if (IsEverything
            || backupPlugins.Any(IncludesPlugin)
            || (DalamudSettings && backupHasDalamudSettings)
            || (GameSettings && backupHasGameSettings)
            || (GameConfig && backupHasGameConfig))
        {
            return null;
        }

        var game = GameSettings || GameConfig;
        return (Plugins, DalamudSettings, game) switch
        {
            ({ Count: 0 }, true, false) => "This backup has no Dalamud settings.",
            ({ Count: 0 }, false, true) => "This backup has no game settings.",
            ({ Count: 0 }, true, true) => "This backup has no Dalamud settings or game settings.",
            _ => "Choose at least one plugin, or Dalamud settings, to restore.",
        };
    }

    /// <summary>Chosen plugin names that <paramref name="backupPlugins"/> doesn't hold, sorted.</summary>
    public IReadOnlyList<string> PluginsMissingFrom(IReadOnlyCollection<string> backupPlugins) =>
        Plugins is null
            ? []
            : Plugins.Where(name => !backupPlugins.Contains(name, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    internal bool Includes(PortableItem item, string relativeTarget) => item switch
    {
        PortableItem.PluginConfig => IncludesPlugin(BackupAllowlist.PluginNameFor(relativeTarget[(BackupAllowlist.PluginConfigsDirectory.Length + 1)..])),
        PortableItem.DalamudConfig or PortableItem.DalamudVfs or PortableItem.DalamudUi => DalamudSettings,
        PortableItem.GameSettings => GameSettings,
        PortableItem.GameConfig => GameConfig,
        _ => false,
    };
}

/// <summary>What the backup holds.</summary>
public sealed record RestoreContents(
    int PluginConfigCount,
    bool DalamudConfig,
    bool DalamudVfs,
    bool DalamudUi,
    int? CustomRepositoryCount,
    bool GameSettings = false,
    bool GameConfig = false,
    int CharacterCount = 0);

/// <summary>What is on this PC right now, for the side-by-side review.</summary>
public sealed record CurrentConfiguration(
    int PluginConfigCount,
    DateTime? PluginConfigsChangedUtc,
    bool DalamudConfig,
    DateTime? DalamudConfigChangedUtc,
    bool DalamudVfs,
    DateTime? DalamudVfsChangedUtc,
    bool DalamudUi,
    int? CustomRepositoryCount);

public sealed record RestorePreview(
    BackupRecord Backup,
    RestoreContents Contents,
    XivLauncherInstallation? Target,
    string? TargetProblem,
    CurrentConfiguration? Current,
    IReadOnlyList<string> PluginsChangedSinceBackup,
    bool DalamudConfigChangedSinceBackup)
{
    /// <summary>True when restoring will roll back settings that changed after the backup was taken.</summary>
    public bool IsOlderThanCurrent => PluginsChangedSinceBackup.Count > 0 || DalamudConfigChangedSinceBackup;

    /// <summary>Why <paramref name="selection"/> can't be restored from this backup, or null when it can.</summary>
    public string? ProblemWith(RestoreSelection selection) =>
        selection.ProblemIn(Backup.PluginNames, Contents.DalamudConfig || Contents.DalamudVfs || Contents.DalamudUi, Contents.GameSettings, Contents.GameConfig);

    /// <summary>The changes made since the backup that restoring <paramref name="selection"/> would roll back.</summary>
    public UndoneChanges ChangesUndoneBy(RestoreSelection selection) =>
        new(PluginsChangedSinceBackup.Where(selection.IncludesPlugin).ToList(), DalamudConfigChangedSinceBackup && selection.DalamudSettings);
}

public sealed record UndoneChanges(IReadOnlyList<string> Plugins, bool DalamudConfig)
{
    public bool Any => Plugins.Count > 0 || DalamudConfig;
}

public enum SafetyCheckId
{
    XivLauncherClosed,
    GameClosed,
    IntegrityVerified,
    DestinationAvailable,
    SnapshotReady,
}

public sealed record SafetyCheck(SafetyCheckId Id, string Label, bool Passed, string Detail);

public enum RestoreStage
{
    VerifyingIntegrity,
    CreatingSafetySnapshot,
    Extracting,
    RestoringPluginConfigs,
    RestoringDalamudSettings,
    RestoringGameSettings,
    Completed,
}

public sealed record RestoreProgress(RestoreStage Stage, double Percent, string Message);

public sealed record RestoreResult(
    int RestoredFileCount,
    int PluginConfigCount,
    bool DalamudConfigRestored,
    bool DalamudVfsRestored,
    bool DalamudUiRestored,
    BackupRecord SafetySnapshot,
    string TargetDataPath,
    TimeSpan Duration,
    bool GameSettingsRestored = false,
    bool GameConfigRestored = false);
