using XIVault.Core.Backup;
using XIVault.Core.Configuration;
using XIVault.Core.Discovery;
using XIVault.Core.Restore;
using XIVault.Core.Scheduling;
using XIVault.Core.State;

namespace XIVault.Core.Status;

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

/// <summary>Everything the Overview screen and <c>xivault status</c> show, gathered in one place.</summary>
public sealed record XivaultStatus(
    XivaultConfig Config,
    LocatorResult Launcher,
    PortableSnapshot? Portable,
    bool DestinationAvailable,
    IReadOnlyList<BackupRecord> Backups,
    ScheduleStatus Schedule,
    RunningApps Running,
    ProtectionState State,
    string? AttentionReason)
{
    public string Destination => Config.BackupDestination!;

    /// <summary>The newest regular backup. Pre-restore snapshots don't count as protection.</summary>
    public BackupRecord? LatestBackup => Backups.FirstOrDefault(record => record.HasManifest && !record.IsSafetySnapshot);

    public long TotalBytes => Backups.Sum(record => record.SizeBytes);

    public bool AutomaticBackupsEnabled => Schedule.Installed && Schedule.InstalledSettings is { Enabled: true };
}

public interface IStatusService
{
    /// <summary>Gathers the current status. With verifyLatest, the newest backup is verified if this PC has not done so yet.</summary>
    Task<XivaultStatus> GetAsync(bool verifyLatest = true, CancellationToken cancellationToken = default);
}

public sealed class StatusService(
    IConfigStore configStore,
    IXivLauncherLocator locator,
    PortableStateScanner scanner,
    IBackupCatalog catalog,
    IBackupScheduler scheduler,
    GameProcessGuard guard) : IStatusService
{
    public async Task<XivaultStatus> GetAsync(bool verifyLatest = true, CancellationToken cancellationToken = default)
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
                var backups = available ? catalog.List(destination).ToList() : [];

                var latestIndex = backups.FindIndex(record => record.HasManifest && !record.IsSafetySnapshot);
                if (verifyLatest && latestIndex >= 0 && backups[latestIndex].Integrity == IntegrityState.Unverified)
                {
                    backups[latestIndex] = catalog.Verify(backups[latestIndex], cancellationToken);
                }

                var (state, reason) = Evaluate(config, launcher, available, latestIndex >= 0 ? backups[latestIndex] : null, schedule);
                return new XivaultStatus(config, launcher, portable, available, backups, schedule, guard.Check(), state, reason);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static (ProtectionState, string?) Evaluate(
        XivaultConfig config,
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
