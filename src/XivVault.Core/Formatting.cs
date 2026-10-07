using System.Globalization;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;

namespace XivVault.Core;

/// <summary>Display formats shared by the CLI and the desktop app, so both say the same thing.</summary>
public static class Formatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    /// <summary>"Today", "Yesterday", "Sep 26" (or "Sep 26, 2025" in another year).</summary>
    public static string Day(DateTime local, DateTime nowLocal)
    {
        var days = (nowLocal.Date - local.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            _ when local.Year == nowLocal.Year => local.ToString("MMM d", CultureInfo.InvariantCulture),
            _ => local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        };
    }

    public static string Time(DateTime local) => local.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>"September 28, 2026 · 1:38 PM".</summary>
    public static string FullDate(DateTime local) =>
        local.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) + " · " + Time(local);

    /// <summary>"Today, 1:38 PM" or "Sep 26, 12:00 PM".</summary>
    public static string DayAndTime(DateTime local, DateTime nowLocal) => $"{Day(local, nowLocal)}, {Time(local)}";

    /// <summary>"2 hours ago", "18 hours ago", "3 days ago", "2 weeks ago".</summary>
    public static string Age(DateTime then, DateTime now)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return span switch
        {
            { TotalHours: < 1 } => Ago((int)span.TotalMinutes, "minute"),
            { TotalDays: < 1 } => Ago((int)span.TotalHours, "hour"),
            { TotalDays: < 14 } => Ago((int)span.TotalDays, "day"),
            { TotalDays: < 60 } => Ago((int)(span.TotalDays / 7), "week"),
            _ => Ago((int)(span.TotalDays / 30), "month"),
        };
    }

    private static string Ago(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")} ago");

    public static string KindLabel(BackupKind? kind, bool longForm = false) => kind switch
    {
        BackupKind.Manual => "Manual",
        BackupKind.Scheduled => "Scheduled",
        BackupKind.PreRestore => longForm ? "Pre-Restore Safety Backup" : "Pre-Restore",
        _ => "Unknown",
    };

    /// <summary>The kind, or "Backup" for an online-only backup whose manifest hasn't been read.</summary>
    public static string KindLabel(BackupRecord record, bool longForm = false) =>
        record is { Kind: null, IsOnlineOnly: true } ? "Backup" : KindLabel(record.Kind, longForm);

    public static string IntegrityLabel(IntegrityState integrity) => integrity switch
    {
        IntegrityState.Verified => "Verified",
        IntegrityState.Failed => "Failed",
        _ => "Unverified",
    };

    /// <summary>The integrity, or "In the cloud" for an online-only backup this PC hasn't verified.</summary>
    public static string IntegrityLabel(BackupRecord record) =>
        record is { IsOnlineOnly: true, Integrity: IntegrityState.Unverified } ? "In the cloud" : IntegrityLabel(record.Integrity);

    /// <summary>"Weekly on Sunday and Wednesday at 12:00 PM", "Daily at 6:00 AM", "At Windows login".</summary>
    public static string Schedule(ScheduleSettings settings)
    {
        var time = Time(DateTime.Today + settings.TimeOfDay.ToTimeSpan());
        return settings.Frequency switch
        {
            ScheduleFrequency.Daily => $"Daily at {time}",
            ScheduleFrequency.AtLogon => "At Windows login",
            _ => $"Weekly on {JoinWords(settings.Days.Order().Select(day => day.ToString()))} at {time}",
        };
    }

    public static string JoinWords(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list[..^1]) + " and " + list[^1],
        };
    }

    public static string Count(int count, string singular, string? plural = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural ?? singular + "s")}");
}
