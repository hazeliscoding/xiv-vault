using System.Diagnostics;
using System.Runtime.InteropServices;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Core.Scheduling;
using XivVault.Core.State;
using XivVault.Core.Status;

namespace XivVault.Core.Diagnostics;

public interface IDiagnosticsService
{
    Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Health checks for the four things that make backups work. Details hold friendly or redacted
/// paths, counts and results, so the report can be shared as-is.
/// </summary>
public sealed class DiagnosticsService(
    IStatusService statusService,
    IAppEnvironment environment,
    PathDisplay paths,
    TimeProvider clock) : IDiagnosticsService
{
    private const long LowSpaceBytes = 1L * 1024 * 1024 * 1024;
    private static readonly TimeSpan StaleBackup = TimeSpan.FromDays(14);

    public async Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetAsync(verifyLatest: true, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => new DiagnosticReport(
                clock.GetUtcNow().UtcDateTime,
                XivVaultInfo.Version,
                RuntimeInformation.OSDescription,
                [.. new[] { Launcher(status), Dalamud(status), GameSettings(status), Destination(status), Scheduling(status) }.Select(Redact)]),
            cancellationToken).ConfigureAwait(false);
    }

    // Error messages quote full paths; the report is meant to be pasted into public issues.
    private DiagnosticGroup Redact(DiagnosticGroup group) => group with
    {
        Checks = [.. group.Checks.Select(check => check with { Label = paths.RedactText(check.Label), Detail = paths.RedactText(check.Detail) })],
    };

    private string Show(string path) => paths.Redact(paths.Friendly(path));

    private DiagnosticGroup Launcher(XivVaultStatus status)
    {
        var checks = new List<DiagnosticCheck>();
        if (!status.Launcher.IsFound)
        {
            checks.Add(new("XIVLauncher folder not found", DiagnosticStatus.Error, status.Launcher.Message));
            return new DiagnosticGroup(DiagnosticArea.XivLauncher, "XIVLauncher", checks);
        }

        var installation = status.Launcher.Installation!;
        checks.Add(new(
            installation.Source == Discovery.InstallationSource.Override ? "Configuration path set in Settings" : "Configuration path found",
            DiagnosticStatus.Healthy,
            Show(installation.RootPath)));
        checks.Add(installation.LauncherExecutable is not null
            ? new("XIVLauncher installed", DiagnosticStatus.Healthy, installation.LauncherVersion is { } version ? "v" + version : "version unknown")
            : new("XIVLauncher app not found", DiagnosticStatus.Warning, "Open XIVLauncher is unavailable"));
        checks.Add(status.Running.Any
            ? new(status.Running.GameRunning ? "FFXIV is running" : "XIVLauncher is running", DiagnosticStatus.Warning, "restores wait until it closes")
            : new("XIVLauncher and FFXIV closed", DiagnosticStatus.Healthy, "restore available"));
        return new DiagnosticGroup(DiagnosticArea.XivLauncher, "XIVLauncher", checks);
    }

    private DiagnosticGroup Dalamud(XivVaultStatus status)
    {
        var checks = new List<DiagnosticCheck>();
        var installation = status.Launcher.Installation;
        if (installation is null)
        {
            checks.Add(new("Dalamud configuration not checked", DiagnosticStatus.Error, "XIVLauncher was not found"));
            return new DiagnosticGroup(DiagnosticArea.Dalamud, "Dalamud", checks);
        }

        var artifacts = installation.Artifacts;
        var missing = installation.HasPortableConfiguration ? DiagnosticStatus.Warning : DiagnosticStatus.Error;
        checks.Add(artifacts.DalamudConfig
            ? new("Configuration file found", DiagnosticStatus.Healthy, BackupAllowlist.DalamudConfigFile)
            : new("Configuration file missing", missing, BackupAllowlist.DalamudConfigFile));
        var pluginFolder = new DirectoryInfo(Path.Combine(installation.DataPath, BackupAllowlist.PluginConfigsDirectory));
        if (artifacts.PluginConfigs && pluginFolder.LinkTarget is not null)
        {
            checks.Add(new("Plugin configuration directory is a link", DiagnosticStatus.Warning, "links are not followed, so plugin settings are not backed up"));
        }
        else
        {
            checks.Add(artifacts.PluginConfigs
                ? new("Plugin configuration directory found", DiagnosticStatus.Healthy, BackupAllowlist.PluginConfigsDirectory + "\\")
                : new("Plugin configuration directory missing", missing, BackupAllowlist.PluginConfigsDirectory + "\\"));
        }
        checks.Add(artifacts.DalamudVfs
            ? new("Plugin database found", DiagnosticStatus.Healthy, BackupAllowlist.DalamudVfsFile)
            : new("Plugin database missing", missing, BackupAllowlist.DalamudVfsFile));
        if (status.Portable is { } portable)
        {
            checks.Add(new(
                Formatting.Count(portable.PluginConfigCount, "plugin config") + " detected",
                DiagnosticStatus.Healthy,
                Formatting.Bytes(portable.TotalBytes)));
        }

        checks.Add(new(
            artifacts.DalamudUi ? "UI layout found" : "No UI layout file",
            DiagnosticStatus.Healthy,
            status.Config.IncludeDalamudUi ? "included in backups" : "not included in backups"));
        if (installation.Layout == Discovery.PortableLayout.DalamudUserData)
        {
            checks.Add(new("User data folder layout", DiagnosticStatus.Healthy, "dalamudUserData\\"));
        }

        return new DiagnosticGroup(DiagnosticArea.Dalamud, "Dalamud", checks);
    }

    /// <summary>Characters are counted, never named: a character folder's name is a content ID.</summary>
    private static DiagnosticGroup GameSettings(XivVaultStatus status)
    {
        var checks = new List<DiagnosticCheck>();
        var game = status.Game;
        if (!game.Found)
        {
            checks.Add(new("No game settings on this PC yet", DiagnosticStatus.Warning, "the game creates them when it first starts"));
            return new DiagnosticGroup(DiagnosticArea.GameSettings, "Game settings", checks);
        }

        checks.Add(status.Config.IncludeGameSettings
            ? new("Game settings folder found", DiagnosticStatus.Healthy, @"Documents\My Games")
            : new("Game settings are not backed up", DiagnosticStatus.Warning, "turn them on in Settings"));
        checks.Add(new(
            game.CharacterCount == 0 ? "No characters yet" : Formatting.Count(game.CharacterCount, "character") + " found",
            DiagnosticStatus.Healthy,
            Formatting.Bytes(game.TotalBytes)));
        checks.Add(game.HasSystemSettings
            ? new("System settings found", DiagnosticStatus.Healthy, BackupAllowlist.GameConfigFile)
            : new("No system settings file", DiagnosticStatus.Healthy, BackupAllowlist.GameConfigFile));
        if (game.LinkedCharacterFolders > 0)
        {
            checks.Add(new(
                game.LinkedCharacterFolders == 1 ? "A character folder is a link" : $"{game.LinkedCharacterFolders} character folders are links",
                DiagnosticStatus.Warning,
                "links are not followed, so those settings are not backed up"));
        }

        return new DiagnosticGroup(DiagnosticArea.GameSettings, "Game settings", checks);
    }

    private DiagnosticGroup Destination(XivVaultStatus status)
    {
        var checks = new List<DiagnosticCheck>();
        var destination = status.Destination;
        var shown = Show(destination);
        if (!status.DestinationAvailable)
        {
            var root = Path.GetPathRoot(destination);
            var creatable = root is not null && Directory.Exists(root);
            checks.Add(creatable
                ? new("Folder will be created by the first backup", DiagnosticStatus.Warning, shown)
                : new("Backup folder not available", DiagnosticStatus.Error, shown + " · drive not connected"));
            return new DiagnosticGroup(DiagnosticArea.BackupDestination, "Backup destination", checks);
        }

        checks.Add(new("Accessible", DiagnosticStatus.Healthy, shown));
        var stopwatch = Stopwatch.StartNew();
        var writable = RestoreService.ProbeWritable(destination, out var problem);
        checks.Add(writable
            ? new("Writable", DiagnosticStatus.Healthy, $"tested {stopwatch.Elapsed.TotalSeconds:0.0} s")
            : new("Not writable", DiagnosticStatus.Error, problem ?? ""));

        checks.Add(FreeSpace(status, destination));

        var latest = status.LatestBackup;
        var now = clock.GetLocalNow().DateTime;
        if (latest is null)
        {
            checks.Add(new("No backups yet", DiagnosticStatus.Warning, "use Back Up Now"));
        }
        else
        {
            var when = Formatting.DayAndTime(latest.CreatedAtUtc.ToLocalTime(), now);
            checks.Add(latest.Integrity switch
            {
                IntegrityState.Failed => new("Latest backup failed verification", DiagnosticStatus.Error, when),
                _ when clock.GetUtcNow().UtcDateTime - latest.CreatedAtUtc > StaleBackup =>
                    new("Latest backup is " + Formatting.Age(latest.CreatedAtUtc, clock.GetUtcNow().UtcDateTime), DiagnosticStatus.Warning, when),
                IntegrityState.Verified => new("Latest backup verified", DiagnosticStatus.Healthy, when),
                _ when latest.IsOnlineOnly => new("Latest backup is in the cloud", DiagnosticStatus.Healthy, when),
                _ => new("Latest backup not verified yet", DiagnosticStatus.Warning, when),
            });
        }

        return new DiagnosticGroup(DiagnosticArea.BackupDestination, "Backup destination", checks);
    }

    private DiagnosticCheck FreeSpace(XivVaultStatus status, string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination))!;
            var drive = new DriveInfo(root);
            var free = Formatting.Bytes(drive.AvailableFreeSpace) + " free";
            var launcherRoot = status.Launcher.Installation is { } installation ? Path.GetPathRoot(installation.RootPath) : null;
            var sameDrive = launcherRoot is not null && string.Equals(launcherRoot, root, StringComparison.OrdinalIgnoreCase);
            if (drive.AvailableFreeSpace < LowSpaceBytes)
            {
                return new(free, DiagnosticStatus.Warning, root.TrimEnd('\\') + " · low on space");
            }

            // A synced folder already keeps a copy elsewhere, so sharing a drive with XIVLauncher is fine there.
            if (sameDrive && !IsSyncedFolder(destination))
            {
                return new(free, DiagnosticStatus.Warning, root.TrimEnd('\\') + " · same drive as XIVLauncher");
            }

            return new(free, DiagnosticStatus.Healthy, root.TrimEnd('\\'));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new("Free space unknown", DiagnosticStatus.Warning, "the drive did not report it");
        }
    }

    public bool IsSyncedFolder(string destination)
    {
        var friendly = paths.Friendly(destination);
        if (friendly.StartsWith("OneDrive", StringComparison.Ordinal) || friendly.StartsWith("Dropbox", StringComparison.Ordinal))
        {
            return true;
        }

        var googleDrive = Path.Combine(environment.UserProfile, "Google Drive");
        return destination.StartsWith(googleDrive, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private DiagnosticGroup Scheduling(XivVaultStatus status)
    {
        var checks = new List<DiagnosticCheck>();
        var schedule = status.Schedule;
        var now = clock.GetLocalNow().DateTime;
        if (schedule.Problem is { } problem && !schedule.Installed)
        {
            checks.Add(new("Task Scheduler unavailable", DiagnosticStatus.Error, problem));
        }
        else if (!schedule.Installed)
        {
            checks.Add(status.Config.Schedule.Enabled
                ? new("Scheduled task missing", DiagnosticStatus.Error, "turn automatic backups off and on again")
                : new("Automatic backups are off", DiagnosticStatus.Warning, "turn them on in Schedule"));
        }
        else
        {
            checks.Add(new("Task installed", DiagnosticStatus.Healthy, WindowsTaskScheduler.TaskName));
            if (schedule.CommandMissing)
            {
                checks.Add(new("Task points to a missing program", DiagnosticStatus.Error, "set the schedule again from this copy of XIV Vault"));
            }

            checks.Add(new(
                "Next run",
                DiagnosticStatus.Healthy,
                schedule.NextRunLocal is { } next ? Formatting.DayAndTime(next, now) : "at next Windows login"));
        }

        if (schedule.LastRun is { } last)
        {
            var when = Formatting.DayAndTime(last.AtUtc.ToLocalTime(), now);
            checks.Add(last.Result switch
            {
                ScheduledRunResult.Success => new("Last run successful", DiagnosticStatus.Healthy, when),
                ScheduledRunResult.Skipped => new("Last run skipped", DiagnosticStatus.Warning, when + " · " + last.Message),
                _ => new("Last run failed", DiagnosticStatus.Error, when + " · " + last.Message),
            });
        }

        return new DiagnosticGroup(DiagnosticArea.Scheduling, "Scheduling", checks);
    }
}
