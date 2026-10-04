using System.IO.Compression;
using Microsoft.Extensions.Logging;
using XIVault.Core.State;

namespace XIVault.Core.Backup;

public interface IBackupCatalog
{
    /// <summary>Archives in <paramref name="destination"/>, newest first. Reads manifests only.</summary>
    IReadOnlyList<BackupRecord> List(string destination);

    /// <summary>Reads one archive, wherever it is. Null when the file is not an XIVault backup.</summary>
    BackupRecord? Read(string archivePath);

    /// <summary>Fully verifies an archive (every hash) and remembers the result.</summary>
    BackupRecord Verify(BackupRecord record, CancellationToken cancellationToken = default);

    /// <summary>Deletes an archive. Only XIVault archives in the given folder can be deleted.</summary>
    void Delete(BackupRecord record, string destination);
}

public sealed class BackupCatalog(ArchiveValidator validator, IStateStore stateStore, TimeProvider clock, ILogger<BackupCatalog> logger)
    : IBackupCatalog
{
    public IReadOnlyList<BackupRecord> List(string destination)
    {
        if (!Directory.Exists(destination))
        {
            return [];
        }

        var verifications = stateStore.Load().Verifications;
        var records = new List<BackupRecord>();
        foreach (var path in Directory.EnumerateFiles(destination, "*" + BackupNaming.Extension))
        {
            var record = ReadRecord(path, verifications);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.LastWriteUtc)
            .ThenByDescending(record => record.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public BackupRecord? Read(string archivePath) =>
        File.Exists(archivePath) ? ReadRecord(Path.GetFullPath(archivePath), stateStore.Load().Verifications) : null;

    public BackupRecord Verify(BackupRecord record, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(record.FilePath);
        if (!info.Exists)
        {
            return record with { Integrity = IntegrityState.Failed, Problem = "The archive no longer exists." };
        }

        var validation = validator.Validate(record.FilePath, verifyContents: true, cancellationToken);
        Remember(info, validation.IsValid, validation.ArchiveSha256, validation.IsValid ? null : validation.Summary);
        logger.LogInformation("Verified {File}: {Result}", info.Name, validation.IsValid ? "valid" : "failed");
        return record with
        {
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Manifest = validation.Manifest ?? record.Manifest,
            Integrity = validation.IsValid ? IntegrityState.Verified : IntegrityState.Failed,
            ArchiveSha256 = validation.ArchiveSha256,
            Problem = validation.IsValid ? null : validation.Summary,
        };
    }

    public void Delete(BackupRecord record, string destination)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var file = Path.GetFullPath(record.FilePath);
        if (!string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, "Only backups in the backup folder can be deleted.");
        }

        if (!record.HasManifest && !BackupNaming.LooksLikeOurs(record.FileName))
        {
            throw new XivaultException(XivaultErrorKind.InvalidConfiguration, $"{record.FileName} is not an XIVault backup.");
        }

        File.Delete(file);
        stateStore.Update(state =>
        {
            state.Verifications.Remove(file);
            return state;
        });
        logger.LogInformation("Deleted {File}", record.FileName);
    }

    /// <summary>Stores a verification result, keyed so that any change to the file invalidates it.</summary>
    internal void Remember(FileInfo info, bool valid, string? archiveSha256, string? problem)
    {
        info.Refresh();
        var entry = new VerificationRecord(info.Length, info.LastWriteTimeUtc, valid, archiveSha256, clock.GetUtcNow().UtcDateTime, problem);
        try
        {
            stateStore.Update(state =>
            {
                state.Verifications[info.FullName] = entry;
                PruneMissing(state);
                return state;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves re-checking later; failing to write it must not fail a backup.
            logger.LogWarning(ex, "Could not record the verification result");
        }
    }

    private static void PruneMissing(AppState state)
    {
        foreach (var path in state.Verifications.Keys.Where(path => !File.Exists(path)).ToList())
        {
            state.Verifications.Remove(path);
        }
    }

    private BackupRecord? ReadRecord(string path, IReadOnlyDictionary<string, VerificationRecord> verifications)
    {
        FileInfo info;
        BackupManifest? manifest = null;
        string? problem = null;
        try
        {
            info = new FileInfo(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            (manifest, var issue) = ArchiveValidator.ReadManifest(zip);
            problem = issue?.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            info = new FileInfo(path);
            problem = $"The archive could not be read: {ex.Message}";
        }

        if (manifest is null)
        {
            // Someone else's ZIP in the same folder is ignored. One of ours that lost its manifest is shown as failed.
            if (!BackupNaming.LooksLikeOurs(info.Name) || !info.Exists)
            {
                return null;
            }

            return new BackupRecord(info.FullName, info.Length, info.LastWriteTimeUtc, null, IntegrityState.Failed, null, problem);
        }

        var integrity = IntegrityState.Unverified;
        string? sha = null;
        if (verifications.TryGetValue(info.FullName, out var cached)
            && cached.Size == info.Length
            && cached.LastWriteUtc == info.LastWriteTimeUtc)
        {
            integrity = cached.Valid ? IntegrityState.Verified : IntegrityState.Failed;
            sha = cached.ArchiveSha256;
            problem = cached.Problem;
        }

        return new BackupRecord(info.FullName, info.Length, info.LastWriteTimeUtc, manifest, integrity, sha, problem);
    }
}
