using Microsoft.Extensions.Logging;

namespace XIVault.Core.Backup;

/// <summary>
/// Removes old archives after a new one has been written and verified. Regular backups and
/// pre-restore snapshots are counted separately, so restores never push out regular backups.
/// </summary>
public sealed class RetentionService(IBackupCatalog catalog, ILogger<RetentionService> logger)
{
    public const int SafetySnapshotsToKeep = 3;

    public IReadOnlyList<string> Apply(string destination, BackupKind createdKind, int retentionCount, string newArchivePath)
    {
        var safety = createdKind == BackupKind.PreRestore;
        var keep = safety ? SafetySnapshotsToKeep : retentionCount;

        // Archives that failed verification neither count toward the limit nor get deleted here:
        // keeping N backups must mean N good ones, and a damaged file is left for the user to inspect.
        var candidates = catalog.List(destination)
            .Where(record => record.HasManifest && record.Integrity != IntegrityState.Failed)
            .Where(record => record.IsSafetySnapshot == safety)
            .ToList();

        var deleted = new List<string>();
        foreach (var record in candidates.Skip(keep))
        {
            if (string.Equals(record.FilePath, newArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                catalog.Delete(record, destination);
                deleted.Add(record.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Retention could not delete {File}", record.FileName);
            }
        }

        if (deleted.Count > 0)
        {
            logger.LogInformation("Retention removed {Count} old archive(s), keeping {Keep}", deleted.Count, keep);
        }

        return deleted;
    }
}
