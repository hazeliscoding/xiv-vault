namespace XivVault.Core.Backup;

public enum IntegrityState
{
    Unverified,
    Verified,
    Failed,
}

/// <summary>An archive in the backup folder, described by its manifest and its last verification.</summary>
public sealed record BackupRecord(
    string FilePath,
    long SizeBytes,
    DateTime LastWriteUtc,
    BackupManifest? Manifest,
    IntegrityState Integrity,
    string? ArchiveSha256,
    string? Problem)
{
    /// <summary>
    /// The file is in the cloud and not on this PC, so its manifest hasn't been read: opening it
    /// would download it. Until then it is described by its file name.
    /// </summary>
    public bool IsOnlineOnly { get; init; }

    /// <summary>The time in the file name. Shown only while the manifest hasn't been read.</summary>
    public DateTime? NamedAtUtc { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public bool HasManifest => Manifest is not null;

    /// <summary>A XIV Vault backup: by its manifest, or by its name while it is online only.</summary>
    public bool IsRecognized => HasManifest || IsOnlineOnly;

    /// <summary>A recognized backup that isn't a safety snapshot. The newest one is "the latest backup".</summary>
    public bool IsRegular => IsRecognized && !IsSafetySnapshot;

    public DateTime CreatedAtUtc => Manifest?.CreatedAtUtc ?? NamedAtUtc ?? LastWriteUtc;

    public BackupKind? Kind => Manifest?.BackupType
        ?? (IsOnlineOnly && FileName.StartsWith(BackupNaming.SafetyPrefix, StringComparison.OrdinalIgnoreCase) ? BackupKind.PreRestore : null);

    public bool IsSafetySnapshot => Kind == BackupKind.PreRestore;

    public int PluginConfigCount => Manifest?.Statistics.PluginConfigCount ?? 0;

    public int CharacterCount => Manifest?.Statistics.CharacterCount ?? 0;

    public IReadOnlyList<string> PluginNames =>
        Manifest is null ? [] : BackupAllowlist.PluginNames(Manifest.Files.Select(file => file.Path));
}
