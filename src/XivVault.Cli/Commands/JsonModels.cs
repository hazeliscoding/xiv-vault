using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Diagnostics;
using XivVault.Core.Scheduling;
using XivVault.Core.State;
using XivVault.Core.Status;

namespace XivVault.Cli.Commands;

/// <summary>The stable shapes behind --json. Field names here are a public contract for scripts.</summary>
internal static class JsonModels
{
    public sealed record BackupJson(
        string File,
        string Path,
        DateTime CreatedAtUtc,
        string Kind,
        long SizeBytes,
        int PluginConfigCount,
        string Integrity,
        string? Problem);

    public sealed record ScheduleJson(
        bool Installed,
        string? Frequency,
        IReadOnlyList<string> Days,
        string? Time,
        DateTime? NextRunLocal,
        LastRunJson? LastRun);

    public sealed record LastRunJson(DateTime AtUtc, string Result, string Message, string? BackupFile);

    public sealed record LauncherJson(bool Found, string? Path, string? Layout, string? Version, bool HasConfiguration, string Message);

    public sealed record StatusJson(
        string Version,
        string State,
        string? AttentionReason,
        LauncherJson XivLauncher,
        int? PluginConfigCount,
        string Destination,
        bool DestinationAvailable,
        BackupJson? LatestBackup,
        int BackupCount,
        long TotalBytes,
        int RetentionCount,
        ScheduleJson Schedule);

    public sealed record CheckJson(string Label, string Status, string Detail);

    public sealed record GroupJson(string Area, string Title, string Status, IReadOnlyList<CheckJson> Checks);

    public sealed record DoctorJson(string Version, DateTime GeneratedAtUtc, string Summary, string Status, IReadOnlyList<GroupJson> Groups);

    public static BackupJson From(BackupRecord record) => new(
        record.FileName,
        record.FilePath,
        record.CreatedAtUtc,
        Kind(record.Kind),
        record.SizeBytes,
        record.PluginConfigCount,
        Formatting.IntegrityLabel(record.Integrity).ToLowerInvariant(),
        record.Problem);

    public static ScheduleJson From(ScheduleStatus status) => new(
        status.Installed,
        status.InstalledSettings is { } settings ? Frequency(settings.Frequency) : null,
        status.InstalledSettings is { Frequency: ScheduleFrequency.Weekly } weekly ? weekly.Days.Select(day => day.ToString()).ToList() : [],
        status.InstalledSettings is { Frequency: not ScheduleFrequency.AtLogon } timed ? timed.Time : null,
        status.NextRunLocal,
        status.LastRun is { } last ? new LastRunJson(last.AtUtc, Result(last.Result), last.Message, last.BackupFile) : null);

    public static StatusJson From(XivVaultStatus status)
    {
        var installation = status.Launcher.Installation;
        return new StatusJson(
            XivVaultInfo.Version,
            State(status.State),
            status.AttentionReason,
            new LauncherJson(
                status.Launcher.IsFound,
                installation?.RootPath,
                installation is null ? null : installation.Layout == Core.Discovery.PortableLayout.DalamudUserData ? "dalamudUserData" : "standard",
                installation?.LauncherVersion,
                installation?.HasPortableConfiguration ?? false,
                status.Launcher.Message),
            status.Portable?.PluginConfigCount,
            status.Destination,
            status.DestinationAvailable,
            status.LatestBackup is { } latest ? From(latest) : null,
            status.Backups.Count,
            status.TotalBytes,
            status.Config.RetentionCount,
            From(status.Schedule));
    }

    public static DoctorJson From(DiagnosticReport report) => new(
        report.XivVaultVersion,
        report.GeneratedAtUtc,
        report.Summary,
        Status(report.Overall),
        report.Groups.Select(group => new GroupJson(
            Area(group.Area),
            group.Title,
            Status(group.Status),
            group.Checks.Select(check => new CheckJson(check.Label, Status(check.Status), check.Detail)).ToList())).ToList());

    public static string Kind(BackupKind? kind) => kind switch
    {
        BackupKind.Manual => "manual",
        BackupKind.Scheduled => "scheduled",
        BackupKind.PreRestore => "preRestore",
        _ => "unknown",
    };

    public static string Frequency(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Daily => "daily",
        ScheduleFrequency.AtLogon => "logon",
        _ => "weekly",
    };

    private static string Result(ScheduledRunResult result) => result switch
    {
        ScheduledRunResult.Success => "success",
        ScheduledRunResult.Skipped => "skipped",
        _ => "failed",
    };

    private static string State(ProtectionState state) => state switch
    {
        ProtectionState.NotDetected => "notDetected",
        ProtectionState.NoConfiguration => "noConfiguration",
        ProtectionState.NotBackedUp => "notBackedUp",
        ProtectionState.Protected => "protected",
        _ => "needsAttention",
    };

    private static string Status(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "healthy",
        DiagnosticStatus.Warning => "warning",
        _ => "error",
    };

    private static string Area(DiagnosticArea area) => area switch
    {
        DiagnosticArea.XivLauncher => "xivLauncher",
        DiagnosticArea.Dalamud => "dalamud",
        DiagnosticArea.BackupDestination => "backupDestination",
        _ => "scheduling",
    };
}
