using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using XIVault.Core.Backup;
using XIVault.Core.Configuration;
using XIVault.Core.Discovery;
using XIVault.Core.Platform;

namespace XIVault.Core.Restore;

public interface IRestoreService
{
    /// <summary>Compares a backup with the current configuration. Changes nothing.</summary>
    Task<RestorePreview> PreviewAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default);

    /// <summary>Runs the checks that must pass before a restore. Changes nothing.</summary>
    Task<IReadOnlyList<SafetyCheck>> CheckAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Replaces one file in the XIVLauncher folder with its staged copy.</summary>
internal interface IRestoreFileWriter
{
    void Replace(string stagedPath, string targetPath);
}

internal sealed class RenameRestoreFileWriter : IRestoreFileWriter
{
    public void Replace(string stagedPath, string targetPath)
    {
        // Copy next to the target first, so the final step is a same-volume rename that either
        // fully happens or doesn't happen at all.
        var temp = targetPath + ".xivault-restore";
        File.Copy(stagedPath, temp, overwrite: true);
        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(stagedPath));
        if (File.Exists(targetPath) && File.GetAttributes(targetPath).HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) & ~FileAttributes.ReadOnly);
        }

        File.Move(temp, targetPath, overwrite: true);
    }
}

public sealed class RestoreService : IRestoreService
{
    private readonly IConfigStore _configStore;
    private readonly IXivLauncherLocator _locator;
    private readonly ArchiveValidator _validator;
    private readonly BackupCatalog _catalog;
    private readonly BackupService _backupService;
    private readonly PortableStateScanner _scanner;
    private readonly GameProcessGuard _guard;
    private readonly PathDisplay _paths;
    private readonly IAppEnvironment _environment;
    private readonly ILogger<RestoreService> _logger;
    private readonly IRestoreFileWriter _writer;

    public RestoreService(
        IConfigStore configStore,
        IXivLauncherLocator locator,
        ArchiveValidator validator,
        BackupCatalog catalog,
        BackupService backupService,
        PortableStateScanner scanner,
        GameProcessGuard guard,
        PathDisplay paths,
        IAppEnvironment environment,
        ILogger<RestoreService> logger)
        : this(configStore, locator, validator, catalog, backupService, scanner, guard, paths, environment, logger, new RenameRestoreFileWriter())
    {
    }

    internal RestoreService(
        IConfigStore configStore,
        IXivLauncherLocator locator,
        ArchiveValidator validator,
        BackupCatalog catalog,
        BackupService backupService,
        PortableStateScanner scanner,
        GameProcessGuard guard,
        PathDisplay paths,
        IAppEnvironment environment,
        ILogger<RestoreService> logger,
        IRestoreFileWriter writer)
    {
        _configStore = configStore;
        _locator = locator;
        _validator = validator;
        _catalog = catalog;
        _backupService = backupService;
        _scanner = scanner;
        _guard = guard;
        _paths = paths;
        _environment = environment;
        _logger = logger;
        _writer = writer;
    }

    public Task<RestorePreview> PreviewAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Preview(archivePath, source), cancellationToken);

    public Task<IReadOnlyList<SafetyCheck>> CheckAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(archivePath, source, cancellationToken), cancellationToken);

    public Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Restore(request, progress, cancellationToken), cancellationToken);

    private RestorePreview Preview(string archivePath, string? source)
    {
        var record = _catalog.Read(archivePath)
            ?? throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"{Path.GetFileName(archivePath)} is not an XIVault backup.");
        var manifest = record.Manifest
            ?? throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, record.Problem ?? "The backup has no readable manifest.");

        int? backupRepos = null;
        if (manifest.Contents.DalamudConfig)
        {
            backupRepos = ReadBackupRepositoryCount(record.FilePath);
        }

        var contents = new RestoreContents(
            manifest.Statistics.PluginConfigCount,
            manifest.Contents.DalamudConfig,
            manifest.Contents.DalamudVfs,
            manifest.Contents.DalamudUi,
            backupRepos);

        var located = _locator.Locate(source);
        if (!located.IsFound)
        {
            return new RestorePreview(record, contents, null, located.Message, null, [], false);
        }

        var target = located.Installation!;
        var current = _scanner.Scan(target.DataPath, includeDalamudUi: true);
        var currentConfig = new CurrentConfiguration(
            current.PluginConfigCount,
            current.LastWriteUtc(PortableItem.PluginConfig),
            current.Has(PortableItem.DalamudConfig),
            current.LastWriteUtc(PortableItem.DalamudConfig),
            current.Has(PortableItem.DalamudVfs),
            current.LastWriteUtc(PortableItem.DalamudVfs),
            current.Has(PortableItem.DalamudUi),
            DalamudConfigReader.CountCustomRepositories(Path.Combine(target.DataPath, BackupAllowlist.DalamudConfigFile)));

        // Only plugins the backup also holds get reverted; plugins it doesn't know are left alone.
        var created = record.CreatedAtUtc;
        var inBackup = new HashSet<string>(record.PluginNames, StringComparer.OrdinalIgnoreCase);
        var changed = current.Files
            .Where(file => file.Item == PortableItem.PluginConfig && file.LastWriteUtc > created)
            .Select(file => BackupAllowlist.PluginNames([file.ArchivePath])[0])
            .Where(inBackup.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var configChanged = manifest.Contents.DalamudConfig && current.LastWriteUtc(PortableItem.DalamudConfig) > created;

        return new RestorePreview(record, contents, target, null, currentConfig, changed, configChanged);
    }

    private IReadOnlyList<SafetyCheck> Check(string archivePath, string? source, CancellationToken cancellationToken)
    {
        var checks = new List<SafetyCheck>();
        var running = _guard.Check();
        checks.Add(new SafetyCheck(
            SafetyCheckId.XivLauncherClosed,
            "XIVLauncher closed",
            !running.LauncherRunning,
            running.LauncherRunning ? "running · close it to continue" : "not running"));
        checks.Add(new SafetyCheck(
            SafetyCheckId.GameClosed,
            "FFXIV closed",
            !running.GameRunning,
            running.GameRunning ? "running · close the game to continue" : "not running"));

        var record = _catalog.Read(archivePath);
        if (record is null)
        {
            checks.Add(new SafetyCheck(SafetyCheckId.IntegrityVerified, "Backup integrity verified", false, "not an XIVault backup"));
        }
        else
        {
            var verified = _catalog.Verify(record, cancellationToken);
            checks.Add(new SafetyCheck(
                SafetyCheckId.IntegrityVerified,
                "Backup integrity verified",
                verified.Integrity == IntegrityState.Verified,
                verified.Integrity == IntegrityState.Verified
                    ? $"sha256 {ArchiveValidator.Short(verified.ArchiveSha256 ?? "")}"
                    : verified.Problem ?? "verification failed"));
        }

        var located = _locator.Locate(source);
        if (!located.IsFound)
        {
            checks.Add(new SafetyCheck(SafetyCheckId.DestinationAvailable, "XIVLauncher folder available", false, "XIVLauncher not found"));
            checks.Add(new SafetyCheck(SafetyCheckId.SnapshotReady, "Safety snapshot ready", false, "nothing to snapshot"));
            return checks;
        }

        var target = located.Installation!;
        var targetWritable = ProbeWritable(target.DataPath, out var targetProblem);
        checks.Add(new SafetyCheck(
            SafetyCheckId.DestinationAvailable,
            "XIVLauncher folder available",
            targetWritable,
            targetWritable ? _paths.Friendly(target.RootPath) : targetProblem!));

        var config = _configStore.Load();
        var destination = config.BackupDestination!;
        var snapshotWritable = ProbeWritable(destination, out var destinationProblem);
        var current = _scanner.Scan(target.DataPath, includeDalamudUi: true);
        checks.Add(new SafetyCheck(
            SafetyCheckId.SnapshotReady,
            "Safety snapshot ready",
            snapshotWritable,
            snapshotWritable
                ? $"{Formatting.Bytes(current.TotalBytes)} to {_paths.Friendly(destination)}"
                : $"backup folder unavailable · {destinationProblem}"));
        return checks;
    }

    private RestoreResult Restore(RestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var config = _configStore.Load();

        progress?.Report(new RestoreProgress(RestoreStage.VerifyingIntegrity, 0, "Verifying backup integrity"));
        var archivePath = Path.GetFullPath(request.ArchivePath);
        var validation = _validator.Validate(archivePath, verifyContents: true, cancellationToken);
        if (File.Exists(archivePath))
        {
            _catalog.Remember(new FileInfo(archivePath), validation.IsValid, validation.ArchiveSha256, validation.IsValid ? null : validation.Summary);
        }

        if (!validation.IsValid)
        {
            throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"The backup can't be restored: {validation.Summary}");
        }

        var manifest = validation.Manifest!;
        var located = _locator.Locate(request.Source);
        if (!located.IsFound)
        {
            throw new XivaultException(XivaultErrorKind.XivLauncherNotFound, located.Message);
        }

        var target = located.Installation!;
        EnsureNothingRunning();

        progress?.Report(new RestoreProgress(RestoreStage.CreatingSafetySnapshot, 10, "Creating pre-restore safety backup"));
        var snapshot = _backupService.Create(target, config.BackupDestination!, BackupKind.PreRestore, includeDalamudUi: true, config, null, cancellationToken);
        _logger.LogInformation("Pre-restore snapshot {File} created", snapshot.Record.FileName);

        var workRoot = Path.Combine(_environment.TempPath, "XIVault", "restore-" + Guid.NewGuid().ToString("N")[..12]);
        var staging = Path.Combine(workRoot, "staged");
        var rollback = Path.Combine(workRoot, "replaced");
        try
        {
            progress?.Report(new RestoreProgress(RestoreStage.Extracting, 30, "Unpacking backup"));
            var staged = Extract(archivePath, manifest, staging, cancellationToken);

            EnsureNothingRunning();
            var applied = Apply(staged, target.DataPath, rollback, manifest, progress, cancellationToken);

            progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Restore complete"));
            _logger.LogInformation("Restored {Count} files into {Path}", applied, target.DataPath);
            return new RestoreResult(
                applied,
                manifest.Statistics.PluginConfigCount,
                manifest.Contents.DalamudConfig,
                manifest.Contents.DalamudVfs,
                manifest.Contents.DalamudUi,
                snapshot.Record,
                target.DataPath,
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not XivaultException)
        {
            throw new XivaultException(
                XivaultErrorKind.Unexpected,
                $"The restore stopped ({ex.Message}). Nothing was changed. Your previous configuration is also saved in {snapshot.Record.FileName}.",
                ex);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private void EnsureNothingRunning()
    {
        var running = _guard.Check();
        if (running.Any)
        {
            var what = running.GameRunning && running.LauncherRunning ? "XIVLauncher and FFXIV are" : running.GameRunning ? "FFXIV is" : "XIVLauncher is";
            throw new XivaultException(XivaultErrorKind.GameRunning, $"{what} running. Close {(running.GameRunning ? "the game" : "XIVLauncher")} before restoring.");
        }
    }

    private sealed record StagedFile(string StagedPath, string RelativeTarget, PortableItem Item);

    /// <summary>
    /// Extracts the manifest's files into a private folder and re-checks every hash. The archive is
    /// never extracted over the XIVLauncher folder, and nothing the manifest doesn't list is touched.
    /// </summary>
    private static List<StagedFile> Extract(string archivePath, BackupManifest manifest, string staging, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staging);
        var staged = new List<StagedFile>();
        using var zip = ZipFile.OpenRead(archivePath);
        var entries = zip.Entries
            .Where(entry => !ArchivePaths.IsDirectoryEntry(entry.FullName))
            .ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BackupAllowlist.TryClassify(file.Path, out var item, out var relativeTarget) || !entries.TryGetValue(file.Path, out var entry))
            {
                throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"Refusing {file.Path}: it is not an allowlisted file.");
            }

            var destination = ArchivePaths.ResolveUnder(staging, relativeTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            {
                CopyBounded(input, output, file.Size);
            }

            using (var check = File.OpenRead(destination))
            {
                var actual = ArchiveValidator.HashBounded(check, file.Size, out var overflow);
                if (overflow || !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"{file.Path} changed while it was unpacked. Nothing was restored.");
                }
            }

            File.SetLastWriteTimeUtc(destination, entry.LastWriteTime.UtcDateTime);
            staged.Add(new StagedFile(destination, relativeTarget, item));
        }

        return staged;
    }

    private int Apply(
        List<StagedFile> staged,
        string dataPath,
        string rollback,
        BackupManifest manifest,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataPath);
        var replaced = new List<(string Target, string? Original)>();
        var createdDirectories = new List<string>();
        var ordered = staged.OrderBy(file => file.Item != PortableItem.PluginConfig).ToList();
        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = ordered[i];
                var percent = 40 + (58.0 * i / Math.Max(1, ordered.Count));
                progress?.Report(file.Item == PortableItem.PluginConfig
                    ? new RestoreProgress(RestoreStage.RestoringPluginConfigs, percent, $"Restoring {manifest.Statistics.PluginConfigCount} plugin configurations")
                    : new RestoreProgress(RestoreStage.RestoringDalamudSettings, percent, "Restoring Dalamud settings"));

                var target = ArchivePaths.ResolveUnder(dataPath, file.RelativeTarget);
                CreateParents(dataPath, target, createdDirectories);

                string? original = null;
                if (File.Exists(target))
                {
                    original = ArchivePaths.ResolveUnder(rollback, file.RelativeTarget);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Copy(target, original, overwrite: false);
                }

                replaced.Add((target, original));
                _writer.Replace(file.StagedPath, target);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed after {Count} file(s); rolling back", replaced.Count);
            var restored = RollBack(replaced, createdDirectories);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new XivaultException(
                XivaultErrorKind.Unexpected,
                restored
                    ? $"The restore stopped ({ex.Message}). Every file it had changed was put back, so your configuration is as it was."
                    : $"The restore stopped ({ex.Message}) and some files could not be put back. Restore the Pre-Restore backup from Backups to return to your previous configuration.",
                ex);
        }

        return ordered.Count;
    }

    private bool RollBack(List<(string Target, string? Original)> replaced, List<string> createdDirectories)
    {
        var ok = true;
        for (var i = replaced.Count - 1; i >= 0; i--)
        {
            var (target, original) = replaced[i];
            try
            {
                TryDelete(target + ".xivault-restore");
                if (original is null)
                {
                    // The restore created this file, so removing it returns the folder to its earlier state.
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else
                {
                    File.Copy(original, target, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
                _logger.LogError(ex, "Rollback could not restore a file");
            }
        }

        foreach (var directory in createdDirectories.AsEnumerable().Reverse())
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
        }

        return ok;
    }

    private static void CreateParents(string dataPath, string target, List<string> created)
    {
        var parent = Path.GetDirectoryName(target)!;
        var missing = new Stack<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataPath));
        while (!Directory.Exists(parent) && parent.Length > root.Length)
        {
            missing.Push(parent);
            parent = Path.GetDirectoryName(parent)!;
        }

        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            created.Add(directory);
        }
    }

    private static void CopyBounded(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, "An archive entry holds more data than its manifest records.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static int? ReadBackupRepositoryCount(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(BackupAllowlist.ArchivePathFor(PortableItem.DalamudConfig));
            if (entry is null || entry.Length > 32 * 1024 * 1024)
            {
                return null;
            }

            using var stream = entry.Open();
            return DalamudConfigReader.CountCustomRepositories(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool ProbeWritable(string folder, out string? problem)
    {
        problem = null;
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".xivault-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = ex is UnauthorizedAccessException ? "not writable" : "not available";
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the temporary restore folder");
        }
    }
}
