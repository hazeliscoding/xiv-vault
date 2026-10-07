using System.IO.Compression;
using Microsoft.Extensions.Logging;
using XivVault.Core.Platform;
using XivVault.Core.State;

namespace XivVault.Core.Backup;

public interface IBackupCatalog
{
    /// <summary>
    /// Archives in <paramref name="destination"/>, newest first. Reads manifests only, and only
    /// from files on this PC; an online-only archive is listed by its name.
    /// </summary>
    IReadOnlyList<BackupRecord> List(string destination);

    /// <summary>
    /// <see cref="List"/>, with the latest backup verified if this PC hasn't checked it and it is on
    /// this PC. Older archives are verified when they are restored, or when asked.
    /// </summary>
    IReadOnlyList<BackupRecord> ListVerifyingLatest(string destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one archive, wherever it is. Null when the file is not a XIV Vault backup. An
    /// online-only file is described without being opened, whatever its name.
    /// </summary>
    BackupRecord? Read(string archivePath);

    /// <summary>
    /// Brings an online-only archive onto this PC by reading it through, then reads its manifest.
    /// <paramref name="progress"/> gets the bytes read so far.
    /// </summary>
    BackupRecord Download(BackupRecord record, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fully verifies an archive (every hash) and remembers the result. An online-only archive is
    /// downloaded first; if that fails, nothing is remembered and the error is thrown.
    /// </summary>
    BackupRecord Verify(BackupRecord record, CancellationToken cancellationToken = default);

    /// <summary>Deletes an archive. Only XIV Vault archives in the given folder can be deleted.</summary>
    void Delete(BackupRecord record, string destination);
}

public sealed class BackupCatalog(
    ArchiveValidator validator,
    IStateStore stateStore,
    IFileAvailability availability,
    TimeProvider clock,
    ILogger<BackupCatalog> logger) : IBackupCatalog
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
            var record = availability.IsOnlineOnly(path)
                ? OnlineOnlyRecord(path, verifications, requireOurName: true)
                : ReadRecord(path, verifications);
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

    public IReadOnlyList<BackupRecord> ListVerifyingLatest(string destination, CancellationToken cancellationToken = default)
    {
        var records = List(destination).ToList();
        var latest = records.FindIndex(record => record.IsRegular);
        if (latest >= 0 && records[latest] is { Integrity: IntegrityState.Unverified, IsOnlineOnly: false })
        {
            records[latest] = Verify(records[latest], cancellationToken);
        }

        return records;
    }

    public BackupRecord? Read(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return null;
        }

        var path = Path.GetFullPath(archivePath);
        var verifications = stateStore.Load().Verifications;
        return availability.IsOnlineOnly(path)
            ? OnlineOnlyRecord(path, verifications, requireOurName: false)
            : ReadRecord(path, verifications);
    }

    public BackupRecord Download(BackupRecord record, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Fetch(record, progress, cancellationToken);
        return ReadRecord(record.FilePath, stateStore.Load().Verifications) switch
        {
            { HasManifest: true } downloaded => downloaded,
            null => throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"{record.FileName} is not a XIV Vault backup."),
            var damaged => throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, damaged.Problem ?? "The backup has no readable manifest."),
        };
    }

    public BackupRecord Verify(BackupRecord record, CancellationToken cancellationToken = default)
    {
        if (record.IsOnlineOnly)
        {
            // A file the sync app can't fetch right now isn't damaged, so that must never be remembered as a failure.
            Fetch(record, null, cancellationToken);
        }

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
            IsOnlineOnly = false,
            Integrity = validation.IsValid ? IntegrityState.Verified : IntegrityState.Failed,
            ArchiveSha256 = validation.ArchiveSha256,
            Problem = validation.IsValid ? null : validation.Summary,
        };
    }

    /// <summary>Reads an online-only file through, which is what makes the sync app download it.</summary>
    private void Fetch(BackupRecord record, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        try
        {
            // Windows has no portable way to ask for a download up front, and reading through
            // reports progress as the bytes arrive.
            using var stream = new FileStream(record.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                progress?.Report(total);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not download {File}", record.FileName);
            throw new XivVaultException(
                XivVaultErrorKind.DestinationUnavailable,
                "The backup couldn't be downloaded. Check that this PC is online and that OneDrive, or the app that syncs the backup folder, is running.",
                ex);
        }

        logger.LogInformation("Downloaded {File}", record.FileName);
    }

    public void Delete(BackupRecord record, string destination)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var file = Path.GetFullPath(record.FilePath);
        if (!string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "Only backups in the backup folder can be deleted.");
        }

        if (!record.HasManifest && !BackupNaming.LooksLikeOurs(record.FileName))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, $"{record.FileName} is not a XIV Vault backup.");
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

        var (integrity, sha, remembered) = Remembered(info, verifications);
        return new BackupRecord(
            info.FullName, info.Length, info.LastWriteTimeUtc, manifest, integrity, sha, integrity == IntegrityState.Unverified ? problem : remembered);
    }

    /// <summary>
    /// Opening an online-only archive would download all of it, so it is described by its name
    /// until a restore asks for it. In a listing only XIV Vault's own names count, as for any ZIP.
    /// </summary>
    private BackupRecord? OnlineOnlyRecord(string path, IReadOnlyDictionary<string, VerificationRecord> verifications, bool requireOurName)
    {
        var info = new FileInfo(path);
        if (requireOurName && !BackupNaming.LooksLikeOurs(info.Name))
        {
            return null;
        }

        var (integrity, sha, problem) = Remembered(info, verifications);
        // A name written in another zone can hold an hour this PC skips for daylight saving.
        var zone = clock.LocalTimeZone;
        var named = BackupNaming.LocalTimeFromName(info.Name) is { } local && !zone.IsInvalidTime(local)
            ? TimeZoneInfo.ConvertTimeToUtc(local, zone)
            : (DateTime?)null;
        return new BackupRecord(info.FullName, info.Length, info.LastWriteTimeUtc, null, integrity, sha, problem)
        {
            IsOnlineOnly = true,
            NamedAtUtc = named,
        };
    }

    private static (IntegrityState Integrity, string? Sha, string? Problem) Remembered(
        FileInfo info, IReadOnlyDictionary<string, VerificationRecord> verifications) =>
        verifications.TryGetValue(info.FullName, out var cached)
        && cached.Size == info.Length
        && cached.LastWriteUtc == info.LastWriteTimeUtc
            ? (cached.Valid ? IntegrityState.Verified : IntegrityState.Failed, cached.ArchiveSha256, cached.Problem)
            : (IntegrityState.Unverified, null, null);
}
