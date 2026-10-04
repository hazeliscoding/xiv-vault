using System.IO.Compression;
using System.Text.Json.Serialization;

namespace XIVault.Core.Configuration;

public sealed record XivaultConfig
{
    public const int DefaultRetentionCount = 10;
    public const int MinRetentionCount = 1;
    public const int MaxRetentionCount = 50;

    public string? BackupDestination { get; init; }

    public int RetentionCount { get; init; } = DefaultRetentionCount;

    public bool IncludeDalamudUi { get; init; }

    public CompressionPreset Compression { get; init; } = CompressionPreset.Balanced;

    /// <summary>Explicit XIVLauncher folder. Null means "detect it".</summary>
    public string? XivLauncherPathOverride { get; init; }

    public ScheduleSettings Schedule { get; init; } = new();
}

public enum CompressionPreset
{
    [JsonStringEnumMemberName("fast")]
    Fast,

    [JsonStringEnumMemberName("balanced")]
    Balanced,

    [JsonStringEnumMemberName("maximum")]
    Maximum,
}

public static class CompressionPresetExtensions
{
    public static CompressionLevel ToCompressionLevel(this CompressionPreset mode) => mode switch
    {
        CompressionPreset.Fast => CompressionLevel.Fastest,
        CompressionPreset.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}

public sealed record ScheduleSettings
{
    public bool Enabled { get; init; }

    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Weekly;

    public IReadOnlyList<DayOfWeek> Days { get; init; } = [DayOfWeek.Sunday];

    /// <summary>Local time of day, 24-hour "HH:mm".</summary>
    public string Time { get; init; } = "12:00";

    public TimeOnly TimeOfDay => TimeOnly.TryParseExact(Time, "HH:mm", out var time) ? time : new TimeOnly(12, 0);
}

public enum ScheduleFrequency
{
    [JsonStringEnumMemberName("daily")]
    Daily,

    [JsonStringEnumMemberName("weekly")]
    Weekly,

    [JsonStringEnumMemberName("logon")]
    AtLogon,
}
