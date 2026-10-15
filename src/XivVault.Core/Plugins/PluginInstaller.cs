using Microsoft.Extensions.Logging;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.Restore;

namespace XivVault.Core.Plugins;

/// <summary>
/// Installs the plugins a restore brings back. <see cref="Prepare"/> downloads and checks every plugin in the
/// restore's work folder before any file changes; <see cref="Place"/> writes them once the settings are restored.
/// Logs hold counts and fixed reasons only: never a plugin's name or ID, a repository, or an exception's text.
/// </summary>
public sealed class PluginInstaller(IWebDownloader downloader, GameProcessGuard guard, ILogger<PluginInstaller> logger)
{
    internal const string OfficialRepository = "https://kamori.goats.dev/Plugin/PluginMaster";

    // Downloads can take minutes, so once anything is requested they get most of the restore's progress bar.
    internal const double DownloadsEnd = 80;
    internal const double PlacingStart = 90;
    private const double DownloadsStart = 10;
    private const double PlacingEnd = 99;

    private const long MaxListBytes = 32 * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const int WriteAttempts = 4;

    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>What restoring <paramref name="settings"/> would install on this PC, worked out without any request.</summary>
    internal static PluginInstallPreview Preview(DalamudPluginSettings settings, string rootPath)
    {
        // Nothing can be told apart in a folder that can't be listed, so every plugin counts as missing.
        var installed = TryScan(rootPath) ?? InstalledPlugins.None;
        return new PluginInstallPreview(
            settings.Plugins.Where(plugin => LocalOutcome(plugin, installed) is null).Select(plugin => plugin.InternalName).ToList(),
            settings.CustomRepositories
                .Select(HttpsHost)
                .OfType<string>()
                .GroupBy(host => host, StringComparer.OrdinalIgnoreCase)
                .Select(host => new CustomRepositoryHost(host.Key, host.Count()))
                .ToList(),
            DalamudInstall.FindVersion(rootPath) is not null);
    }

    /// <summary>
    /// Downloads and checks, in <paramref name="workFolder"/>, each plugin the restore installs that this PC is
    /// missing, changing nothing outside it. Nothing is requested unless a plugin is missing and Dalamud is ready.
    /// Cancelling throws <see cref="OperationCanceledException"/>, and nothing outside the work folder has changed then.
    /// </summary>
    internal PreparedPlugins Prepare(
        string configPath,
        string rootPath,
        Func<string, bool> installs,
        IReadOnlyCollection<string> tickedHosts,
        string workFolder,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = DalamudConfigReader.ReadPluginSettings(configPath) ?? new DalamudPluginSettings([], [], false, new HashSet<string>());
        var chosen = settings.Plugins
            .Where(plugin => installs(plugin.InternalName))
            .DistinctBy(plugin => plugin.InternalName, StringComparer.Ordinal)
            .ToList();
        var outcomes = new Dictionary<string, PluginInstallOutcome>(StringComparer.Ordinal);
        var staged = new List<StagedPlugin>();
        var requested = false;
        if (TryScan(rootPath) is not { } installed)
        {
            logger.LogWarning("The installed plugins couldn't be listed, so no plugin is installed");
            return Skip(chosen, PluginInstallOutcome.WriteFailed);
        }

        var missing = new List<PluginReference>();
        foreach (var plugin in chosen)
        {
            if (LocalOutcome(plugin, installed) is { } outcome)
            {
                outcomes[plugin.InternalName] = outcome;
            }
            else
            {
                missing.Add(plugin);
            }
        }

        logger.LogInformation("{Missing} of {Chosen} chosen plugin(s) are missing", missing.Count, chosen.Count);
        if (missing.Count == 0)
        {
            return Prepared();
        }

        if (DalamudInstall.FindVersion(rootPath) is not { } dalamud)
        {
            logger.LogInformation("Dalamud isn't ready yet, so no plugin is installed");
            return Skip(missing, PluginInstallOutcome.DalamudNotReady);
        }

        // A slow list can take minutes too, so the downloads stage starts before the first request.
        progress?.Report(new RestoreProgress(RestoreStage.DownloadingPlugins, DownloadsStart, "Downloading plugin lists"));
        requested = true;

        // Dalamud lists nothing from custom repositories when the official list fails, so neither does this.
        if (FetchList(OfficialRepository, cancellationToken) is not { } official)
        {
            logger.LogWarning("The official plugin list couldn't be read, so no plugin is installed");
            return Skip(missing, PluginInstallOutcome.RepositoryUnavailable);
        }

        var custom = settings.CustomRepositories
            .Where(url => HttpsHost(url) is { } host && tickedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            .Select(url => new CustomRepositoryList(url, FetchList(url, cancellationToken)))
            .ToList();
        if (custom.Count(repository => repository.Plugins is null) is > 0 and var unavailable)
        {
            logger.LogWarning("{Count} custom plugin list(s) couldn't be read", unavailable);
        }

        var bans = DalamudInstall.LoadBanList(rootPath);
        var plan = InstallPlanner.Plan(missing, installed, dalamud, official, custom, bans, settings.DoPluginTest, settings.TestingOptIns);
        foreach (var skipped in plan.Skipped)
        {
            outcomes[skipped.InternalName] = skipped.Outcome;
        }

        var diskFull = false;
        for (var i = 0; i < plan.Installs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var install = plan.Installs[i];
            progress?.Report(new RestoreProgress(
                RestoreStage.DownloadingPlugins,
                DownloadsStart + ((DownloadsEnd - DownloadsStart) * (i + 1) / (plan.Installs.Count + 1)),
                $"Downloading plugin {i + 1} of {plan.Installs.Count}"));
            var name = install.Plugin.InternalName;
            if (diskFull || !IsFolderName(install.Entry.InternalName))
            {
                outcomes[name] = diskFull ? PluginInstallOutcome.WriteFailed : PluginInstallOutcome.PackageRefused;
                continue;
            }

            // Named by position, so no path holds a name from a repository until it is checked.
            var zip = Path.Combine(workFolder, $"{i}.zip");
            var folder = Path.Combine(workFolder, $"{i}");
            try
            {
                Directory.CreateDirectory(workFolder);
                using (var file = new FileStream(zip, FileMode.CreateNew, FileAccess.ReadWrite))
                {
                    downloader.Download(install.DownloadLink, "application/zip", file, MaxPackageBytes, cancellationToken);
                }

                outcomes[name] = PluginPackage.Stage(zip, install, dalamud, folder);
                if (outcomes[name] == PluginInstallOutcome.Installed)
                {
                    staged.Add(new StagedPlugin(install, folder));
                }
            }
            catch (WebDownloadException)
            {
                outcomes[name] = PluginInstallOutcome.DownloadFailed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes[name] = PluginInstallOutcome.WriteFailed;
                diskFull = ex.HResult is DiskFull or HandleDiskFull;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A package that breaks the checks in a way they don't expect is refused like any other bad package.
                outcomes[name] = PluginInstallOutcome.PackageRefused;
            }
            finally
            {
                TryDelete(zip);
            }
        }

        logger.LogInformation("Downloaded and checked {Staged} of {Planned} plugin(s)", staged.Count, plan.Installs.Count);
        return Prepared();

        PreparedPlugins Prepared() =>
            new(rootPath, chosen.Select(plugin => new PluginOutcome(plugin.InternalName, outcomes[plugin.InternalName])).ToList(), staged, requested);

        PreparedPlugins Skip(IEnumerable<PluginReference> plugins, PluginInstallOutcome outcome)
        {
            foreach (var plugin in plugins)
            {
                outcomes[plugin.InternalName] = outcome;
            }

            return Prepared();
        }
    }

    /// <summary>
    /// Writes each prepared plugin into installedPlugins. Never throws: the settings are already restored, so a
    /// plugin that can't be written is listed with its reason, and the restore still completes.
    /// </summary>
    internal PluginInstallReport Place(PreparedPlugins prepared, IProgress<RestoreProgress>? progress)
    {
        var written = new Dictionary<string, PluginInstallOutcome>(StringComparer.Ordinal);
        try
        {
            var gameStarted = false;
            for (var i = 0; i < prepared.Staged.Count; i++)
            {
                var plugin = prepared.Staged[i];
                progress?.Report(new RestoreProgress(
                    RestoreStage.InstallingPlugins,
                    PlacingStart + ((PlacingEnd - PlacingStart) * i / prepared.Staged.Count),
                    $"Installing plugin {i + 1} of {prepared.Staged.Count}"));

                // Checked rather than refused: what is already written stays, and the rest are listed.
                gameStarted = gameStarted || guard.Check().Any;
                written[plugin.Install.Plugin.InternalName] = gameStarted ? PluginInstallOutcome.GameStarted : Write(prepared.RootPath, plugin);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Installing plugins stopped unexpectedly ({Error})", ex.GetType().Name);
        }

        // A staged plugin the loop never reached wasn't written.
        var outcomes = prepared.Outcomes
            .Select(plugin => plugin.Outcome != PluginInstallOutcome.Installed ? plugin
                : plugin with { Outcome = written.GetValueOrDefault(plugin.InternalName, PluginInstallOutcome.WriteFailed) })
            .ToList();
        var report = new PluginInstallReport(
            outcomes.Where(plugin => plugin.Outcome == PluginInstallOutcome.Installed).Select(plugin => plugin.InternalName).ToList(),
            outcomes.Where(plugin => plugin.Outcome is not (PluginInstallOutcome.Installed or PluginInstallOutcome.AlreadyInstalled)).ToList(),
            outcomes.Count(plugin => plugin.Outcome == PluginInstallOutcome.AlreadyInstalled));
        logger.LogInformation(
            "Installed {Installed} plugin(s), {Already} already installed, not installed: {Reasons}",
            report.Installed.Count,
            report.AlreadyInstalled,
            report.NotInstalled.Count == 0 ? "none" : string.Join(", ", report.NotInstalled.GroupBy(plugin => plugin.Outcome).Select(group => $"{group.Key} {group.Count()}")));
        return report;
    }

    /// <summary>
    /// The planner's first rule, which needs no repository: why a plugin can't be installed whatever is offered,
    /// or null when it can be.
    /// </summary>
    private static PluginInstallOutcome? LocalOutcome(PluginReference plugin, InstalledPlugins installed) =>
        installed.IsLink ? PluginInstallOutcome.LinkedFolder : installed.StateOf(plugin.InternalName) switch
        {
            null => installed.Ids.Contains(plugin.WorkingPluginId) ? PluginInstallOutcome.IdInUse : null,
            PluginFolderState.Installed => PluginInstallOutcome.AlreadyInstalled,
            PluginFolderState.Link => PluginInstallOutcome.LinkedFolder,
            _ => PluginInstallOutcome.InTheWay,
        };

    private static InstalledPlugins? TryScan(string rootPath)
    {
        try
        {
            return InstalledPluginScanner.Scan(rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // installedPlugins can't be listed.
            return null;
        }
    }

    /// <summary>
    /// The punycode host of an HTTPS repository, or null. Dalamud also accepts plain HTTP repositories; XIV Vault
    /// doesn't. Uri accepts some hosts it can't convert, such as one holding a pasted soft hyphen, and those are left out.
    /// </summary>
    private static string? HttpsHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            return uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // Paths are built from a plugin's name, so it must be a single plain folder name.
    private static bool IsFolderName(string name) => ArchivePaths.IsSafe(name) && !name.Contains('/');

    /// <summary>Null when the list can't be downloaded or read, as Dalamud then lists nothing from it.</summary>
    private IReadOnlyList<RepositoryPlugin>? FetchList(string url, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        try
        {
            downloader.Download(url, "application/json", body, MaxListBytes, cancellationToken);
        }
        catch (WebDownloadException)
        {
            return null;
        }

        body.Position = 0;
        return PluginRepositoryParser.Parse(body);
    }

    /// <summary>
    /// Copies one staged plugin into <c>installedPlugins\&lt;Name&gt;\&lt;Version&gt;\</c>, its manifest last, so a
    /// crash leaves a folder without one, which Dalamud deletes at its next start. Copies rather than moves, so the
    /// files get the folder's permissions, which Dalamud's opt-in sandbox needs. On failure, removes only what it wrote.
    /// </summary>
    private PluginInstallOutcome Write(string rootPath, StagedPlugin plugin)
    {
        var name = plugin.Install.Entry.InternalName;
        if (!IsFolderName(name))
        {
            return PluginInstallOutcome.PackageRefused;
        }

        var created = new List<string>();
        try
        {
            // Checked again: anything may have changed since the scan, which was before the downloads.
            var root = new DirectoryInfo(Path.Combine(rootPath, "installedPlugins"));
            var folder = new DirectoryInfo(Path.Combine(root.FullName, name));
            if (root.LinkTarget is not null || folder.LinkTarget is not null)
            {
                return PluginInstallOutcome.LinkedFolder;
            }

            if (folder.Exists && folder.EnumerateFileSystemInfos().Any())
            {
                return PluginInstallOutcome.InTheWay;
            }

            // The staged manifest keeps the zip's spelling of its name, as every other file does.
            var manifest = Directory.EnumerateFiles(plugin.Folder).Single(file => Path.GetFileName(file).Equals(name + ".json", StringComparison.OrdinalIgnoreCase));
            var target = Path.Combine(folder.FullName, plugin.Install.Version.ToString());
            CreateFolder(target, created);
            foreach (var file in Directory.EnumerateFiles(plugin.Folder, "*", SearchOption.AllDirectories).Where(file => file != manifest))
            {
                Copy(file, Path.Combine(target, Path.GetRelativePath(plugin.Folder, file)), created);
            }

            var finalManifest = Path.Combine(target, Path.GetFileName(manifest));
            var temporary = finalManifest + ".xiv-vault-restore";
            Copy(manifest, temporary, created);
            Retry(() => File.Move(temporary, finalManifest));
            return PluginInstallOutcome.Installed;
        }
        catch (Exception ex)
        {
            Undo(created);
            logger.LogWarning("A plugin couldn't be written ({Error})", ex.GetType().Name);
            return PluginInstallOutcome.WriteFailed;
        }
    }

    // Records each folder it creates, so a failed install removes only those.
    private static void CreateFolder(string path, List<string> created)
    {
        var missing = new Stack<string>();
        for (var folder = path; folder is not null && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            missing.Push(folder);
        }

        while (missing.Count > 0)
        {
            var folder = missing.Pop();
            Directory.CreateDirectory(folder);
            created.Add(folder);
        }
    }

    private static void Copy(string source, string destination, List<string> created)
    {
        CreateFolder(Path.GetDirectoryName(destination)!, created);

        // The version folder is new, so a file already here was put there by something else and is never removed.
        if (!File.Exists(destination))
        {
            created.Add(destination);
        }

        Retry(() => File.Copy(source, destination));
    }

    // An antivirus scan or the search indexer holds a new file for a moment.
    private static void Retry(Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex) when (attempt < WriteAttempts && (ex is UnauthorizedAccessException || ex.HResult is SharingViolation or LockViolation))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    // Newest first, so files go before their folders, and a folder goes only once it is empty.
    private static void Undo(List<string> created)
    {
        for (var i = created.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(created[i]))
                {
                    File.Delete(created[i]);
                }
                else if (Directory.Exists(created[i]) && !Directory.EnumerateFileSystemEntries(created[i]).Any())
                {
                    Directory.Delete(created[i]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // A zip still held open, by an antivirus scan say, goes with the work folder at the end of the restore.
    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A plugin downloaded, checked and extracted into a folder of its own in the work folder.</summary>
internal sealed record StagedPlugin(PlannedInstall Install, string Folder);

/// <summary>
/// What <see cref="PluginInstaller.Prepare"/> leaves for <see cref="PluginInstaller.Place"/>: every chosen
/// plugin's outcome, in order, where <see cref="PluginInstallOutcome.Installed"/> marks one staged to be written.
/// <c>Requested</c> is true once anything was requested, which takes the progress bar into the downloads' share.
/// </summary>
internal sealed record PreparedPlugins(string RootPath, IReadOnlyList<PluginOutcome> Outcomes, IReadOnlyList<StagedPlugin> Staged, bool Requested);
