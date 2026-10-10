using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Core.Scheduling;
using XivVault.Core.State;

namespace XivVault.Core.Status;

public enum ProtectionState
{
    /// <summary>No XIVLauncher folder.</summary>
    NotDetected,

    /// <summary>XIVLauncher exists but Dalamud has saved nothing yet. Restoring is possible.</summary>
    NoConfiguration,

    NotBackedUp,
    Protected,
    NeedsAttention,
}

/// <summary>
/// The game's own settings folder. Characters are counted, never named: a character folder's name
/// is a content ID that identifies the character.
/// </summary>
public sealed record GameSettingsState(
    string FolderPath,
    bool Found,
    int CharacterCount,
    bool HasSystemSettings,
    long TotalBytes,
    int LinkedCharacterFolders);

/// <summary>Everything the Overview screen and <c>xiv-vault status</c> show, gathered in one place.</summary>
public sealed record XivVaultStatus(
    XivVaultConfig Config,
    LocatorResult Launcher,
    PortableSnapshot? Portable,
    bool DestinationAvailable,
    IReadOnlyList<BackupRecord> Backups,
    ScheduleStatus Schedule,
    RunningApps Running,
    ProtectionState State,
    string? AttentionReason,
    GameSettingsState Game)
{
    public string Destination => Config.BackupDestination!;

    /// <summary>The newest regular backup. Pre-restore snapshots don't count as protection.</summary>
    public BackupRecord? LatestBackup => Backups.FirstOrDefault(record => record.IsRegular);

    public long TotalBytes => Backups.Sum(record => record.SizeBytes);

    public bool AutomaticBackupsEnabled => Schedule.Installed && Schedule.InstalledSettings is { Enabled: true };
}

public interface IStatusService
{
    /// <summary>Gathers the current status. With verifyLatest, the newest backup is verified if this PC has not done so yet.</summary>
    Task<XivVaultStatus> GetAsync(bool verifyLatest = true, CancellationToken cancellationToken = default);
}

public sealed class StatusService(
    IConfigStore configStore,
    IXivLauncherLocator locator,
    PortableStateScanner scanner,
    IBackupCatalog catalog,
    IBackupScheduler scheduler,
    GameProcessGuard guard,
    IAppEnvironment environment) : IStatusService
{
    public async Task<XivVaultStatus> GetAsync(bool verifyLatest = true, CancellationToken cancellationToken = default)
    {
        var config = configStore.Load();
        var schedule = await scheduler.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                var launcher = locator.Locate();
                var portable = launcher.Installation is { HasPortableConfiguration: true } installation
                    ? scanner.Scan(installation.DataPath, config.IncludeDalamudUi)
                    : null;
                var destination = config.BackupDestination!;
                var available = Directory.Exists(destination);
                IReadOnlyList<BackupRecord> backups = !available ? []
                    : verifyLatest ? catalog.ListVerifyingLatest(destination, cancellationToken)
                    : catalog.List(destination);

                var (state, reason) = Evaluate(config, launcher, available, backups.FirstOrDefault(record => record.IsRegular), schedule);
                var game = ReadGameSettings(GameSettingsFolder.In(environment));
                return new XivVaultStatus(config, launcher, portable, available, backups, schedule, guard.Check(), state, reason, game);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private GameSettingsState ReadGameSettings(string folder)
    {
        var scan = scanner.ScanGame(folder);
        return new GameSettingsState(
            folder,
            Directory.Exists(folder),
            BackupAllowlist.CharacterCount(scan.Files.Select(file => file.ArchivePath)),
            scan.Files.Any(file => file.Item == PortableItem.SystemSettings),
            scan.Files.Sum(file => file.Size),
            scan.LinkedCharacterFolders);
    }

    private static (ProtectionState, string?) Evaluate(
        XivVaultConfig config,
        LocatorResult launcher,
        bool destinationAvailable,
        BackupRecord? latest,
        ScheduleStatus schedule)
    {
        if (!launcher.IsFound)
        {
            return (ProtectionState.NotDetected, launcher.Message);
        }

        if (!launcher.Installation!.HasPortableConfiguration)
        {
            return (ProtectionState.NoConfiguration, "Dalamud has not saved any configuration on this PC yet.");
        }

        if (latest is null)
        {
            return (ProtectionState.NotBackedUp, null);
        }

        if (!destinationAvailable)
        {
            return (ProtectionState.NeedsAttention, "The backup folder is not available.");
        }

        if (latest.Integrity == IntegrityState.Failed)
        {
            return (ProtectionState.NeedsAttention, "The latest backup did not pass verification.");
        }

        if (config.Schedule.Enabled && !schedule.Installed)
        {
            return (ProtectionState.NeedsAttention, "Automatic backups are on, but the Windows task is missing.");
        }

        if (schedule.LastRun is { Result: ScheduledRunResult.Failed } failed && failed.AtUtc > latest.CreatedAtUtc)
        {
            return (ProtectionState.NeedsAttention, "The last scheduled backup failed: " + failed.Message);
        }

        return (ProtectionState.Protected, null);
    }
}
