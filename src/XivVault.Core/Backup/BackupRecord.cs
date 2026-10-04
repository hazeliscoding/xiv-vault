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
    public string FileName => Path.GetFileName(FilePath);

    public bool HasManifest => Manifest is not null;

    public DateTime CreatedAtUtc => Manifest?.CreatedAtUtc ?? LastWriteUtc;

    public BackupKind? Kind => Manifest?.BackupType;

    public bool IsSafetySnapshot => Kind == BackupKind.PreRestore;

    public int PluginConfigCount => Manifest?.Statistics.PluginConfigCount ?? 0;

    public IReadOnlyList<string> PluginNames =>
        Manifest is null ? [] : BackupAllowlist.PluginNames(Manifest.Files.Select(file => file.Path));
}
