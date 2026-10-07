using Microsoft.Extensions.Logging;

namespace XivVault.Core.Backup;

/// <summary>
/// Removes old archives after a new one has been written and verified. Regular backups and
/// pre-restore snapshots are counted separately, so restores never push out regular backups.
/// </summary>
public sealed class RetentionService(IBackupCatalog catalog, ILogger<RetentionService> logger)
{
    public const int SafetySnapshotsToKeep = 3;

    /// <param name="destination">The backup folder.</param>
    /// <param name="createdKind">The kind just created, which picks the group to trim.</param>
    /// <param name="retentionCount">How many regular backups to keep.</param>
    /// <param name="protectedPaths">Archives that must survive, such as the one being restored.</param>
    public IReadOnlyList<string> Apply(string destination, BackupKind createdKind, int retentionCount, IReadOnlyCollection<string> protectedPaths)
    {
        var safety = createdKind == BackupKind.PreRestore;
        var keep = safety ? SafetySnapshotsToKeep : retentionCount;
        var candidates = catalog.List(destination)
            .Where(record => record.IsRecognized && record.IsSafetySnapshot == safety)
            .ToList();
        if (candidates.Count(record => record.Integrity != IntegrityState.Failed) <= keep)
        {
            return [];
        }

        var kept = 0;
        var deleted = new List<string>();
        foreach (var listed in candidates)
        {
            // Something is about to be deleted. First check each archive this PC hasn't verified, so
            // one that was damaged after it was written can't take the place of a good backup. An
            // online-only archive is never downloaded for this: among the newest it is skipped,
            // and past the limit it is deleted unopened, since enough verified backups are newer.
            var record = listed is { Integrity: IntegrityState.Unverified, IsOnlineOnly: false } ? catalog.Verify(listed) : listed;
            if (kept < keep)
            {
                if (record.Integrity == IntegrityState.Verified)
                {
                    kept++;
                }

                continue;
            }

            // Damaged archives neither count toward the limit nor get deleted: keeping N backups
            // must mean N good ones, and a damaged file is left for the user to inspect.
            if (record.Integrity == IntegrityState.Failed)
            {
                continue;
            }

            if (protectedPaths.Contains(record.FilePath, StringComparer.OrdinalIgnoreCase))
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
