using XivVault.Core.Backup;
using XivVault.Core.Discovery;

namespace XivVault.Core.Restore;

public sealed record RestoreRequest(string ArchivePath)
{
    /// <summary>Overrides the configured XIVLauncher folder for this restore.</summary>
    public string? Source { get; init; }
}

/// <summary>What the backup holds.</summary>
public sealed record RestoreContents(
    int PluginConfigCount,
    bool DalamudConfig,
    bool DalamudVfs,
    bool DalamudUi,
    int? CustomRepositoryCount);

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
    TimeSpan Duration);
