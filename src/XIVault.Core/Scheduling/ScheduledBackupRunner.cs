using Microsoft.Extensions.Logging;
using XIVault.Core.Backup;
using XIVault.Core.Restore;
using XIVault.Core.State;

namespace XIVault.Core.Scheduling;

public sealed record ScheduledRunOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxWait { get; init; } = TimeSpan.FromHours(6);
}

public sealed record ScheduledRunOutcome(ScheduledRunResult Result, string Message, BackupResult? Backup, XivaultErrorKind? Error);

/// <summary>
/// What the scheduled task runs: the same backup as Back Up Now, after waiting for FFXIV to close,
/// with the result recorded for the Schedule screen and diagnostics.
/// </summary>
public sealed class ScheduledBackupRunner(
    IBackupService backups,
    GameProcessGuard guard,
    IStateStore stateStore,
    TimeProvider clock,
    ILogger<ScheduledBackupRunner> logger)
{
    public async Task<ScheduledRunOutcome> RunAsync(ScheduledRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ScheduledRunOptions();
        var waited = TimeSpan.Zero;
        while (guard.Check().GameRunning)
        {
            if (waited >= options.MaxWait)
            {
                return Record(ScheduledRunResult.Skipped, $"Skipped: FFXIV was still running after {options.MaxWait.TotalHours:0.#} hours.", null, XivaultErrorKind.GameRunning);
            }

            if (waited == TimeSpan.Zero)
            {
                logger.LogInformation("FFXIV is running; waiting for it to close before backing up");
            }

            await Task.Delay(options.PollInterval, clock, cancellationToken).ConfigureAwait(false);
            waited += options.PollInterval;
        }

        try
        {
            var result = await backups.CreateBackupAsync(new BackupRequest(BackupKind.Scheduled), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Record(ScheduledRunResult.Success, "Successful", result, null);
        }
        catch (XivaultException ex)
        {
            logger.LogError(ex, "Scheduled backup failed");
            return Record(ScheduledRunResult.Failed, ex.Message, null, ex.Kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Scheduled backup failed");
            return Record(ScheduledRunResult.Failed, ex.Message, null, XivaultErrorKind.Unexpected);
        }
    }

    private ScheduledRunOutcome Record(ScheduledRunResult result, string message, BackupResult? backup, XivaultErrorKind? error)
    {
        var record = new ScheduledRunRecord(clock.GetUtcNow().UtcDateTime, result, message, backup?.Record.FileName);
        stateStore.Update(state => state with { LastScheduledRun = record });
        logger.LogInformation("Scheduled run finished: {Result}", result);
        return new ScheduledRunOutcome(result, message, backup, error);
    }
}
