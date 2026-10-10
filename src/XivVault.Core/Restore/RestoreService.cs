using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;

namespace XivVault.Core.Restore;

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
        var temp = targetPath + ".xiv-vault-restore";
        File.Copy(stagedPath, temp, overwrite: true);
        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(stagedPath));
        var readOnly = File.Exists(targetPath) && File.GetAttributes(targetPath).HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
        {
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) & ~FileAttributes.ReadOnly);
        }

        File.Move(temp, targetPath, overwrite: true);
        if (readOnly)
        {
            File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
        }
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
    private readonly RetentionService _retention;
    private readonly OperationLock _operationLock;
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
        RetentionService retention,
        OperationLock operationLock,
        ILogger<RestoreService> logger)
        : this(configStore, locator, validator, catalog, backupService, scanner, guard, paths, environment, retention, operationLock, logger, new RenameRestoreFileWriter())
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
        RetentionService retention,
        OperationLock operationLock,
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
        _retention = retention;
        _operationLock = operationLock;
        _logger = logger;
        _writer = writer;
    }

    public Task<RestorePreview> PreviewAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    return Preview(archivePath, source, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"The backup could not be read ({ex.Message}).", ex);
                }
            },
            cancellationToken);

    public Task<IReadOnlyList<SafetyCheck>> CheckAsync(string archivePath, string? source = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(archivePath, source, cancellationToken), cancellationToken);

    public Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Restore(request, progress, cancellationToken), cancellationToken);

    private RestorePreview Preview(string archivePath, string? source, CancellationToken cancellationToken)
    {
        var record = _catalog.Read(archivePath)
            ?? throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"{Path.GetFileName(archivePath)} is not a XIV Vault backup.");
        if (record.IsOnlineOnly)
        {
            // The front ends download first, showing the size and progress. This only makes sure a
            // preview never mistakes an archive that hasn't been read for one without a manifest.
            record = _catalog.Download(record, null, cancellationToken);
        }

        var manifest = record.Manifest
            ?? throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, record.Problem ?? "The backup has no readable manifest.");

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
            backupRepos,
            manifest.Contents.CharacterSettings,
            manifest.Contents.SystemSettings,
            manifest.Statistics.CharacterCount);

        var located = _locator.Locate(source);
        if (!located.IsFound)
        {
            return new RestorePreview(record, contents, null, located.Message, null, [], false);
        }

        var target = located.Installation!;
        var current = _scanner.Scan(target.DataPath, includeDalamudUi: true, GameSettingsFolder.In(_environment));
        var currentConfig = new CurrentConfiguration(
            current.PluginConfigCount,
            current.LastWriteUtc(PortableItem.PluginConfig),
            current.Has(PortableItem.DalamudConfig),
            current.LastWriteUtc(PortableItem.DalamudConfig),
            current.Has(PortableItem.DalamudVfs),
            current.LastWriteUtc(PortableItem.DalamudVfs),
            current.Has(PortableItem.DalamudUi),
            DalamudConfigReader.CountCustomRepositories(Path.Combine(target.DataPath, BackupAllowlist.DalamudConfigFile)),
            current.CharacterCount,
            current.LastWriteUtc(PortableItem.CharacterSettings),
            current.Has(PortableItem.SystemSettings),
            current.LastWriteUtc(PortableItem.SystemSettings));

        // A file counts as changed when it was written after the backup and differs from the copy in
        // it. The date alone isn't enough: plugins and Dalamud often save their settings unchanged.
        // Files the backup doesn't hold are left alone by a restore, so they never count.
        var created = record.CreatedAtUtc;
        var backupHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            backupHashes.TryAdd(file.Path, file.Sha256);
        }

        bool ChangedSinceBackup(PortableFile file) =>
            file.LastWriteUtc > created
            && backupHashes.TryGetValue(file.ArchivePath, out var hash)
            && !string.Equals(HashOrNull(file.SourcePath), hash, StringComparison.OrdinalIgnoreCase);

        var changed = current.Files
            .Where(file => file.Item == PortableItem.PluginConfig && ChangedSinceBackup(file))
            .Select(file => BackupAllowlist.PluginNames([file.ArchivePath])[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var configChanged = current.Files.Any(file => file.Item == PortableItem.DalamudConfig && ChangedSinceBackup(file));

        return new RestorePreview(record, contents, target, null, currentConfig, changed, configChanged);
    }

    /// <summary>The file's SHA-256, or null when it can't be read; an unreadable file counts as changed.</summary>
    private static string? HashOrNull(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
            checks.Add(new SafetyCheck(SafetyCheckId.IntegrityVerified, "Backup integrity verified", false, "not a XIV Vault backup"));
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
        var current = _scanner.Scan(target.DataPath, includeDalamudUi: true, GameSettingsFolder.In(_environment));
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
        using var held = _operationLock.Acquire();
        var stopwatch = Stopwatch.StartNew();
        var config = _configStore.Load();

        progress?.Report(new RestoreProgress(RestoreStage.VerifyingIntegrity, 0, "Verifying backup integrity"));
        var archivePath = Path.GetFullPath(request.ArchivePath);

        // Held open with read sharing only, so nothing (retention, a sync client, another process)
        // can replace or delete the archive between checking it and unpacking it.
        FileStream archive;
        try
        {
            archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"The backup could not be opened ({ex.Message}).", ex);
        }

        using var archiveHandle = archive;
        var validation = _validator.Validate(archive, verifyContents: true, cancellationToken);
        if (validation.IsValid)
        {
            archive.Position = 0;
            validation = validation with { ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(archive)) };
        }

        _catalog.Remember(new FileInfo(archivePath), validation.IsValid, validation.ArchiveSha256, validation.IsValid ? null : validation.Summary);
        if (!validation.IsValid)
        {
            throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"The backup can't be restored: {validation.Summary}");
        }

        var manifest = validation.Manifest!;
        var located = _locator.Locate(request.Source);
        if (!located.IsFound)
        {
            throw new XivVaultException(XivVaultErrorKind.XivLauncherNotFound, located.Message);
        }

        var target = located.Installation!;
        var gamePath = GameSettingsFolder.In(_environment);
        var selection = request.Selection;
        var pluginCount = CheckSelection(selection, manifest);
        EnsureNoLinks(target.DataPath, gamePath, manifest, selection);
        EnsureNothingRunning();

        progress?.Report(new RestoreProgress(RestoreStage.CreatingSafetySnapshot, 10, "Creating pre-restore safety backup"));
        var snapshot = _backupService.Create(
            target, config.BackupDestination!, BackupKind.PreRestore, includeDalamudUi: true, config, null, cancellationToken, applyRetention: false);
        _logger.LogInformation("Pre-restore snapshot {File} created", snapshot.Record.FileName);

        var workRoot = Path.Combine(_environment.TempPath, "XIV Vault", "restore-" + Guid.NewGuid().ToString("N")[..12]);
        var staging = Path.Combine(workRoot, "staged");
        var rollback = Path.Combine(workRoot, "replaced");
        try
        {
            progress?.Report(new RestoreProgress(RestoreStage.Extracting, 30, "Unpacking backup"));
            archive.Position = 0;
            // Every file is unpacked and checked again, chosen or not, so a damaged archive is
            // refused as a whole; only the chosen files are then written.
            var staged = Extract(archive, manifest, staging, cancellationToken);
            var chosen = staged.Where(file => selection.Includes(file.Item, file.RelativeTarget)).ToList();

            EnsureNothingRunning();
            var applied = Apply(chosen, target.DataPath, gamePath, rollback, pluginCount, progress, cancellationToken);

            progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Restore complete"));
            _logger.LogInformation("Restored {Count} files into {Path}", applied, target.DataPath);
            return new RestoreResult(
                applied,
                pluginCount,
                manifest.Contents.DalamudConfig && selection.DalamudSettings,
                manifest.Contents.DalamudVfs && selection.DalamudSettings,
                manifest.Contents.DalamudUi && selection.DalamudSettings,
                snapshot.Record,
                target.DataPath,
                stopwatch.Elapsed,
                manifest.Contents.CharacterSettings && selection.CharacterSettings,
                manifest.Contents.SystemSettings && selection.SystemSettings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not XivVaultException)
        {
            throw new XivVaultException(
                XivVaultErrorKind.Unexpected,
                $"The restore stopped ({ex.Message}). Nothing was changed. Your previous configuration is also saved in {snapshot.Record.FileName}.",
                ex);
        }
        finally
        {
            TryDeleteDirectory(workRoot);

            // Snapshot retention runs only now, and never removes the snapshot just taken or the
            // archive being restored, even when that archive is itself the oldest snapshot.
            try
            {
                _retention.Apply(
                    Path.GetDirectoryName(snapshot.Record.FilePath)!,
                    BackupKind.PreRestore,
                    config.RetentionCount,
                    [snapshot.Record.FilePath, archivePath]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XivVaultException)
            {
                _logger.LogWarning(ex, "Safety snapshot retention was skipped");
            }
        }
    }

    /// <summary>
    /// Refuses a choice that names a plugin the backup doesn't hold, or that would write nothing,
    /// before anything changes. Returns how many plugins will be restored.
    /// </summary>
    private int CheckSelection(RestoreSelection selection, BackupManifest manifest)
    {
        var plugins = BackupAllowlist.PluginNames(manifest.Files.Select(file => file.Path));
        var contents = manifest.Contents;
        if (selection.ProblemIn(plugins, contents.DalamudConfig || contents.DalamudVfs || contents.DalamudUi, contents.CharacterSettings, contents.SystemSettings) is { } problem)
        {
            throw new XivVaultException(XivVaultErrorKind.InvalidConfiguration, problem);
        }

        var chosen = plugins.Count(selection.IncludesPlugin);
        _logger.LogInformation(
            "Restoring {Chosen} of {Total} plugin(s); Dalamud settings {Dalamud}; character settings {Character}; system settings {System}",
            chosen,
            plugins.Count,
            Chosen(selection.DalamudSettings),
            Chosen(selection.CharacterSettings),
            Chosen(selection.SystemSettings));
        return chosen;

        static string Chosen(bool chosen) => chosen ? "chosen" : "not chosen";
    }

    /// <summary>
    /// Backups never follow links under the Dalamud folder or the game's settings folder, so a
    /// restore must not write through one either: the safety snapshot would not have saved what lies
    /// behind it.
    /// </summary>
    private static void EnsureNoLinks(string dataPath, string gamePath, BackupManifest manifest, RestoreSelection selection)
    {
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!BackupAllowlist.TryClassify(file.Path, out var item, out var relative) || !selection.Includes(item, relative))
            {
                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BackupAllowlist.IsGame(item) ? gamePath : dataPath));
            var target = ArchivePaths.ResolveUnder(root, relative);
            if (new FileInfo(target).LinkTarget is not null)
            {
                throw LinkRefused(target);
            }

            for (var folder = Path.GetDirectoryName(target); folder is not null && folder.Length > root.Length; folder = Path.GetDirectoryName(folder))
            {
                if (checkedFolders.Add(folder) && new DirectoryInfo(folder).LinkTarget is not null)
                {
                    throw LinkRefused(folder);
                }
            }
        }

        static XivVaultException LinkRefused(string path) => new(
            XivVaultErrorKind.RestoreValidationFailed,
            $"{BackupAllowlist.ForDisplay(path)} is a link to another location. XIV Vault does not back up or restore through links; replace it with a normal folder or file and try again.");
    }

    private void EnsureNothingRunning()
    {
        var running = _guard.Check();
        if (running.Any)
        {
            var what = running.GameRunning && running.LauncherRunning ? "XIVLauncher and FFXIV are" : running.GameRunning ? "FFXIV is" : "XIVLauncher is";
            throw new XivVaultException(XivVaultErrorKind.GameRunning, $"{what} running. Close {(running.GameRunning ? "the game" : "XIVLauncher")} before restoring.");
        }
    }

    private sealed record StagedFile(string StagedPath, string RelativeTarget, PortableItem Item);

    /// <summary>Keeps game files apart from Dalamud files in the staging and rollback folders.</summary>
    private static string AreaPath(PortableItem item, string relativeTarget) =>
        (BackupAllowlist.IsGame(item) ? "game/" : "dalamud/") + relativeTarget;

    /// <summary>
    /// Extracts the manifest's files into a private folder and re-checks every hash. The archive is
    /// never extracted over the XIVLauncher folder, and nothing the manifest doesn't list is touched.
    /// </summary>
    private static List<StagedFile> Extract(Stream archive, BackupManifest manifest, string staging, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staging);
        var staged = new List<StagedFile>();
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries
            .Where(entry => !ArchivePaths.IsDirectoryEntry(entry.FullName))
            .ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BackupAllowlist.TryClassify(file.Path, out var item, out var relativeTarget) || !entries.TryGetValue(file.Path, out var entry))
            {
                throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"Refusing {BackupAllowlist.ForDisplay(file.Path)}: it is not an allowlisted file.");
            }

            var destination = ArchivePaths.ResolveUnder(staging, AreaPath(item, relativeTarget));
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
                    throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, $"{BackupAllowlist.ForDisplay(file.Path)} changed while it was unpacked. Nothing was restored.");
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
        string gamePath,
        string rollback,
        int pluginCount,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataPath);
        var replaced = new List<(string Target, string? Original)>();
        var createdDirectories = new List<string>();

        // Plugin configs, then Dalamud settings, then game settings, matching the stages the UI shows.
        var ordered = staged
            .OrderBy(file => file.Item == PortableItem.PluginConfig ? 0 : BackupAllowlist.IsGame(file.Item) ? 2 : 1)
            .ToList();
        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = ordered[i];
                var game = BackupAllowlist.IsGame(file.Item);
                var percent = 40 + (58.0 * i / Math.Max(1, ordered.Count));
                progress?.Report(
                    file.Item == PortableItem.PluginConfig ? new RestoreProgress(RestoreStage.RestoringPluginConfigs, percent, $"Restoring {Formatting.Count(pluginCount, "plugin configuration")}")
                    : game ? new RestoreProgress(RestoreStage.RestoringGameSettings, percent, "Restoring game settings")
                    : new RestoreProgress(RestoreStage.RestoringDalamudSettings, percent, "Restoring Dalamud settings"));

                var target = ArchivePaths.ResolveUnder(game ? gamePath : dataPath, file.RelativeTarget);

                // On a PC where the game never started, the game folder and My Games don't exist yet.
                // They are created like any other missing folder, so a failed restore removes them again.
                CreateParents(game ? _environment.Documents : dataPath, target, createdDirectories);

                string? original = null;
                if (File.Exists(target))
                {
                    original = ArchivePaths.ResolveUnder(rollback, AreaPath(file.Item, file.RelativeTarget));
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

            throw new XivVaultException(
                XivVaultErrorKind.Unexpected,
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
                File.Delete(target + ".xiv-vault-restore");
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                _logger.LogWarning(ex, "Rollback could not remove a temporary file");
            }

            try
            {
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
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    }

                    File.Copy(original, target, overwrite: true);
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
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
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                _logger.LogWarning(ex, "Rollback could not remove a folder it created");
            }
        }

        return ok;
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

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
                throw new XivVaultException(XivVaultErrorKind.RestoreValidationFailed, "An archive entry holds more data than its manifest records.");
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
            var probe = Path.Combine(folder, $".xiv-vault-probe-{Guid.NewGuid():N}");
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

    /// <summary>The temp folder holds copies of configuration, so read-only files must not keep it alive.</summary>
    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            _logger.LogWarning(ex, "Could not remove the temporary restore folder");
        }
    }
}
