using System.Text;
using Microsoft.Extensions.Logging;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;
using XivVault.Core.State;

namespace XivVault.Core.Scheduling;

public interface IBackupScheduler
{
    Task<ScheduleStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task InstallAsync(ScheduleSettings settings, ScheduledCommand command, CancellationToken cancellationToken = default);

    Task RemoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows Task Scheduler through schtasks.exe. Only the task XML is read back; the next run is
/// computed here and the last run is recorded by the run itself, because schtasks' text output
/// is localized.
/// </summary>
public sealed class WindowsTaskScheduler(
    ICommandRunner runner,
    IAppEnvironment environment,
    IStateStore stateStore,
    TimeProvider clock,
    ILogger<WindowsTaskScheduler> logger) : IBackupScheduler
{
    public const string TaskName = "XIV Vault Scheduled Backup";
    private const string Schtasks = "schtasks.exe";

    public async Task<ScheduleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var lastRun = stateStore.Load().LastScheduledRun;
        CommandResult result;
        try
        {
            result = await runner.RunAsync(Schtasks, ["/Query", "/TN", TaskName, "/XML", "ONE"], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ScheduleStatus(false, null, null, null, lastRun, $"Task Scheduler is not available ({ex.Message}).");
        }

        if (result.ExitCode != 0)
        {
            return new ScheduleStatus(false, null, null, null, lastRun, null);
        }

        var (settings, command) = TaskXml.Parse(result.StandardOutput);
        var next = settings is { Enabled: true } ? NextRun.After(settings, clock.GetLocalNow().DateTime) : null;
        return new ScheduleStatus(true, settings, command, next, lastRun, settings is null ? "The task exists but its schedule was not recognized." : null);
    }

    public async Task InstallAsync(ScheduleSettings settings, ScheduledCommand command, CancellationToken cancellationToken = default)
    {
        var xml = TaskXml.Build(settings, command, environment.UserAccount, clock.GetLocalNow().DateTime);
        var file = Path.Combine(environment.TempPath, $"xiv-vault-task-{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(environment.TempPath);

        // schtasks reads task XML as UTF-16, matching the declaration.
        await File.WriteAllTextAsync(file, xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await runner.RunAsync(Schtasks, ["/Create", "/TN", TaskName, "/XML", file, "/F"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new XivVaultException(
                    XivVaultErrorKind.Unexpected,
                    $"Task Scheduler refused the backup task: {FirstLine(result.StandardError, result.StandardOutput)}");
            }

            logger.LogInformation("Installed scheduled task ({Frequency})", settings.Frequency);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Installed)
        {
            return;
        }

        var result = await runner.RunAsync(Schtasks, ["/Delete", "/TN", TaskName, "/F"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new XivVaultException(
                XivVaultErrorKind.Unexpected,
                $"Task Scheduler could not remove the backup task: {FirstLine(result.StandardError, result.StandardOutput)}");
        }

        logger.LogInformation("Removed scheduled task");
    }

    private static string FirstLine(params string[] outputs) =>
        outputs.Select(output => output.Trim()).FirstOrDefault(output => output.Length > 0)?.Split('\n')[0].Trim() ?? "no details";
}
