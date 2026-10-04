using System.Text.Json.Serialization;

namespace XIVault.Core.Backup;

/// <summary>
/// The authoritative description of an archive, stored as <c>manifest.json</c> at its root.
/// See docs/backup-format.md before changing anything here.
/// </summary>
public sealed record BackupManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    public string XivaultVersion { get; init; } = "";

    public DateTime CreatedAtUtc { get; init; }

    public BackupKind BackupType { get; init; }

    public ManifestSource Source { get; init; } = new();

    public ManifestContents Contents { get; init; } = new();

    public ManifestStatistics Statistics { get; init; } = new();

    public IReadOnlyList<ManifestFile> Files { get; init; } = [];
}

public enum BackupKind
{
    [JsonStringEnumMemberName("manual")]
    Manual,

    [JsonStringEnumMemberName("scheduled")]
    Scheduled,

    [JsonStringEnumMemberName("preRestore")]
    PreRestore,
}

public sealed record ManifestSource
{
    public string Platform { get; init; } = "windows";

    /// <summary>"standard" or "dalamudUserData". Informational: restore follows the target's layout.</summary>
    public string Layout { get; init; } = "standard";
}

public sealed record ManifestContents
{
    public bool PluginConfigs { get; init; }

    public bool DalamudConfig { get; init; }

    public bool DalamudVfs { get; init; }

    public bool DalamudUi { get; init; }
}

public sealed record ManifestStatistics
{
    /// <summary>Distinct plugins with settings in pluginConfigs (a JSON file, a folder, or both).</summary>
    public int PluginConfigCount { get; init; }

    public int PluginConfigDirectories { get; init; }

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }
}

public sealed record ManifestFile
{
    /// <summary>Archive path with forward slashes, always under <c>payload/</c>.</summary>
    public string Path { get; init; } = "";

    public long Size { get; init; }

    /// <summary>Lowercase hex SHA-256 of the uncompressed file.</summary>
    public string Sha256 { get; init; } = "";
}
