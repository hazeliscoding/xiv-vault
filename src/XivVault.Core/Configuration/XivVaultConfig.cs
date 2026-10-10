using System.IO.Compression;
using System.Text.Json.Serialization;

namespace XivVault.Core.Configuration;

// Properties are settable, not init: source-generated System.Text.Json gives a missing init
// property its type's default, so a settings file from an older version would lose these defaults.
public sealed record XivVaultConfig
{
    public const int DefaultRetentionCount = 10;
    public const int MinRetentionCount = 1;
    public const int MaxRetentionCount = 50;

    public string? BackupDestination { get; set; }

    public int RetentionCount { get; set; } = DefaultRetentionCount;

    public bool IncludeDalamudUi { get; set; }

    /// <summary>The game's own settings from Documents\My Games. Safety snapshots hold them either way.</summary>
    public bool IncludeGameSettings { get; set; } = true;

    public CompressionPreset Compression { get; set; } = CompressionPreset.Balanced;

    /// <summary>Explicit XIVLauncher folder. Null means "detect it".</summary>
    public string? XivLauncherPathOverride { get; set; }

    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>Whether the installed desktop app asks GitHub for a newer version when it opens.</summary>
    public bool CheckForUpdates { get; set; } = true;
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

// Settable for the same reason as XivVaultConfig.
public sealed record ScheduleSettings
{
    public bool Enabled { get; set; }

    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;

    public IReadOnlyList<DayOfWeek> Days { get; set; } = [DayOfWeek.Sunday];

    /// <summary>Local time of day, 24-hour "HH:mm".</summary>
    public string Time { get; set; } = "12:00";

    [JsonIgnore]
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
