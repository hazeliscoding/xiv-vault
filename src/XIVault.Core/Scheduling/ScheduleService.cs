using XIVault.Core.Configuration;

namespace XIVault.Core.Scheduling;

/// <summary>
/// Keeps the saved schedule preferences and the Windows task in step. Turning the schedule off
/// removes the task but keeps the preferences, so turning it back on restores them.
/// </summary>
public sealed class ScheduleService(IConfigStore configStore, IBackupScheduler scheduler)
{
    public Task<ScheduleStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        scheduler.GetStatusAsync(cancellationToken);

    public async Task<ScheduleStatus> ApplyAsync(ScheduleSettings settings, ScheduledCommand command, CancellationToken cancellationToken = default)
    {
        var config = configStore.Load() with { Schedule = settings };
        ConfigStore.Validate(config);
        if (settings.Enabled)
        {
            await scheduler.InstallAsync(settings, command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await scheduler.RemoveAsync(cancellationToken).ConfigureAwait(false);
        }

        configStore.Save(config);
        return await scheduler.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleStatus> DisableAsync(CancellationToken cancellationToken = default)
    {
        var config = configStore.Load();
        return await ApplyAsync(config.Schedule with { Enabled = false }, new ScheduledCommand("", ""), cancellationToken).ConfigureAwait(false);
    }
}
