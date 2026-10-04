using System.Globalization;

namespace XIVault.Core;

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

    public static string Count(int count, string singular, string? plural = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural ?? singular + "s")}");
}
