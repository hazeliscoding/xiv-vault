using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
using XivVault.Core.Serialization;

namespace XivVault.Core.Backup;

public sealed record BackupRequest(BackupKind Kind)
{
    /// <summary>Overrides the configured backup folder for this run.</summary>
    public string? Destination { get; init; }

    /// <summary>Overrides the configured XIVLauncher folder for this run.</summary>
    public string? Source { get; init; }
}

public enum BackupStage
{
    Scanning,
    SnapshottingSettings,
    CompressingPluginConfigs,
    Verifying,
    Finalizing,
    Completed,
}

public sealed record BackupProgress(BackupStage Stage, double Percent, string Message);

public sealed record BackupResult(BackupRecord Record, IReadOnlyList<string> RemovedByRetention, TimeSpan Duration)
{
    public BackupManifest Manifest => Record.Manifest!;
}

public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class BackupService(
    IConfigStore configStore,
    IXivLauncherLocator locator,
    PortableStateScanner scanner,
    ArchiveValidator validator,
    BackupCatalog catalog,
    RetentionService retention,
    PathDisplay paths,
    OperationLock operationLock,
    TimeProvider clock,
    ILogger<BackupService> logger) : IBackupService
{
    public Task<BackupResult> CreateBackupAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Kind == BackupKind.PreRestore)
        {
            throw new ArgumentException("Pre-restore snapshots are created by the restore engine.", nameof(request));
        }

        return Task.Run(
            () =>
            {
                var config = configStore.Load();
                var located = locator.Locate(request.Source);
                if (!located.IsFound)
                {
                    throw new XivVaultException(XivVaultErrorKind.XivLauncherNotFound, located.Message);
                }

                var installation = located.Installation!;
                if (!installation.HasPortableConfiguration)
                {
                    throw new XivVaultException(
                        XivVaultErrorKind.XivLauncherNotFound,
                        $"XIVLauncher was found at {installation.RootPath}, but there is no Dalamud configuration to back up yet. Start the game once with Dalamud enabled.");
                }

                var destination = request.Destination ?? config.BackupDestination!;
                return Create(installation, destination, request.Kind, config.IncludeDalamudUi, config, progress, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// Writes, verifies and finalizes one archive, then applies retention. Also used by restore for
    /// the pre-restore snapshot, which always includes the UI layout and may be empty on a fresh PC;
    /// restore applies snapshot retention itself, after it is done with the archive it restores.
    /// </summary>
    internal BackupResult Create(
        XivLauncherInstallation installation,
        string destination,
        BackupKind kind,
        bool includeDalamudUi,
        XivVaultConfig config,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken,
        bool applyRetention = true)
    {
        using var held = operationLock.Acquire();
        var stopwatch = Stopwatch.StartNew();
        var reporter = new ProgressReporter(progress);
        reporter.Report(BackupStage.Scanning, 0, "Scanning plugin configurations");

        PortableSnapshot snapshot;
        try
        {
            snapshot = scanner.Scan(installation.DataPath, includeDalamudUi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XivVaultException(XivVaultErrorKind.Unexpected, $"The Dalamud files could not be read ({ex.Message}).", ex);
        }
        if (snapshot.Files.Count == 0 && kind != BackupKind.PreRestore)
        {
            throw new XivVaultException(
                XivVaultErrorKind.XivLauncherNotFound,
                $"There are no Dalamud configuration files in {installation.DataPath} to back up.");
        }

        destination = PrepareDestination(destination);
        var finalName = BackupNaming.NewFileName(destination, kind, clock.GetLocalNow());
        var finalPath = Path.Combine(destination, finalName);
        var tempPath = finalPath + BackupNaming.TempSuffix;
        logger.LogInformation("Creating {Kind} backup {File} from {Count} files", kind, finalName, snapshot.Files.Count);

        ArchiveValidation validation;
        try
        {
            WriteArchive(tempPath, snapshot, installation, kind, config.Compression, reporter, cancellationToken);

            reporter.Report(BackupStage.Verifying, 86, "Verifying hashes");
            validation = validator.Validate(tempPath, verifyContents: true, cancellationToken);
            if (!validation.IsValid)
            {
                throw new XivVaultException(
                    XivVaultErrorKind.BackupValidationFailed,
                    $"The new backup failed verification and was discarded: {validation.Summary}");
            }

            reporter.Report(BackupStage.Finalizing, 96, $"Saving to {paths.Friendly(destination)}");
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (Exception ex)
        {
            // Nothing that failed may look like a finished backup.
            TryDelete(tempPath);
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new XivVaultException(XivVaultErrorKind.Unexpected, $"The backup could not be completed ({ex.Message}).", ex);
            }

            throw;
        }

        // The backup is finished and verified from here on; nothing below may report it as failed.
        catalog.Remember(new FileInfo(finalPath), valid: true, validation.ArchiveSha256, problem: null);
        IReadOnlyList<string> removed = [];
        if (applyRetention)
        {
            try
            {
                removed = retention.Apply(destination, kind, config.RetentionCount, [finalPath]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XivVaultException)
            {
                logger.LogWarning(ex, "Retention was skipped");
            }
        }

        var record = catalog.Read(finalPath)
            ?? new BackupRecord(finalPath, new FileInfo(finalPath).Length, File.GetLastWriteTimeUtc(finalPath), validation.Manifest, IntegrityState.Verified, validation.ArchiveSha256, null);
        reporter.Report(BackupStage.Completed, 100, "Backup complete");
        logger.LogInformation("Backup {File} finished in {Elapsed} ms", finalName, stopwatch.ElapsedMilliseconds);
        return new BackupResult(record, removed, stopwatch.Elapsed);
    }

    private string PrepareDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, "The backup folder must be a full path, such as D:\\XIV Vault.");
        }

        try
        {
            var full = Path.GetFullPath(destination);
            Directory.CreateDirectory(full);
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XivVaultException(
                XivVaultErrorKind.DestinationUnavailable,
                $"The backup folder {destination} is not available ({ex.Message}). Check that the drive is connected.",
                ex);
        }
    }

    private void WriteArchive(
        string tempPath,
        PortableSnapshot snapshot,
        XivLauncherInstallation installation,
        BackupKind kind,
        CompressionPreset compression,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        FileStream output;
        try
        {
            output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XivVaultException(XivVaultErrorKind.DestinationUnavailable, $"Could not write to the backup folder ({ex.Message}).", ex);
        }

        using (output)
        {
            var written = new List<(ManifestFile File, PortableItem Item)>();
            var totalBytes = Math.Max(1, snapshot.TotalBytes);
            long doneBytes = 0;
            var pluginCount = snapshot.PluginConfigCount;
            var level = compression.ToCompressionLevel();

            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Dalamud settings first, then plugin configs, matching the stages the UI shows.
                foreach (var file in snapshot.Files.OrderBy(file => file.Item == PortableItem.PluginConfig))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stage = file.Item == PortableItem.PluginConfig ? BackupStage.CompressingPluginConfigs : BackupStage.SnapshottingSettings;
                    var label = stage == BackupStage.CompressingPluginConfigs
                        ? $"Compressing {pluginCount} configurations"
                        : "Snapshotting Dalamud settings";
                    reporter.Report(stage, 4 + (80.0 * doneBytes / totalBytes), label);

                    var entry = AddFile(zip, file, level);
                    if (entry is not null)
                    {
                        written.Add((entry, file.Item));
                    }

                    doneBytes += file.Size;
                }

                var manifest = BuildManifest(written, snapshot, installation, kind);
                var manifestEntry = zip.CreateEntry(BackupAllowlist.ManifestEntryName, CompressionLevel.Optimal);
                using var manifestStream = manifestEntry.Open();
                JsonSerializer.Serialize(manifestStream, manifest, XivVaultJsonContext.Default.BackupManifest);
            }

            output.Flush(flushToDisk: true);
        }
    }

    private ManifestFile? AddFile(ZipArchive zip, PortableFile file, CompressionLevel level)
    {
        FileStream source;
        try
        {
            // Dalamud may hold its files open; share access so a manual backup works while it runs.
            source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            logger.LogWarning("A file disappeared during the backup and was skipped");
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            logger.LogWarning("A folder disappeared during the backup and was skipped");
            return null;
        }

        using (source)
        {
            var entry = zip.CreateEntry(file.ArchivePath, level);
            entry.LastWriteTime = ClampToZipRange(file.LastWriteUtc);
            using var target = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                target.Write(buffer, 0, read);
                size += read;
            }

            return new ManifestFile { Path = file.ArchivePath, Size = size, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) };
        }
    }

    private BackupManifest BuildManifest(
        List<(ManifestFile File, PortableItem Item)> written,
        PortableSnapshot snapshot,
        XivLauncherInstallation installation,
        BackupKind kind)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            XivVaultVersion = XivVaultInfo.Version,
            CreatedAtUtc = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc),
            BackupType = kind,
            Source = new ManifestSource
            {
                Platform = "windows",
                Layout = installation.Layout == PortableLayout.DalamudUserData ? "dalamudUserData" : "standard",
            },
            Contents = new ManifestContents
            {
                PluginConfigs = written.Any(item => item.Item == PortableItem.PluginConfig),
                DalamudConfig = written.Any(item => item.Item == PortableItem.DalamudConfig),
                DalamudVfs = written.Any(item => item.Item == PortableItem.DalamudVfs),
                DalamudUi = written.Any(item => item.Item == PortableItem.DalamudUi),
            },
            Statistics = new ManifestStatistics
            {
                PluginConfigCount = BackupAllowlist.PluginNames(written.Select(item => item.File.Path)).Count,
                PluginConfigDirectories = snapshot.PluginConfigDirectories,
                FileCount = written.Count,
                TotalBytes = written.Sum(item => item.File.Size),
            },
            Files = written.Select(item => item.File).ToList(),
        };
    }

    // ZIP stores DOS timestamps, which start in 1980.
    private static DateTimeOffset ClampToZipRange(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : new DateTimeOffset(local);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the unfinished archive {File}", Path.GetFileName(path));
        }
    }

    private sealed class ProgressReporter(IProgress<BackupProgress>? progress)
    {
        private BackupStage? _stage;
        private int _percent = -1;

        public void Report(BackupStage stage, double percent, string message)
        {
            var whole = (int)Math.Clamp(percent, 0, 100);
            if (progress is null || (stage == _stage && whole == _percent))
            {
                return;
            }

            _stage = stage;
            _percent = whole;
            progress.Report(new BackupProgress(stage, whole, message));
        }
    }
}
