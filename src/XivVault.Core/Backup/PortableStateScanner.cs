using Microsoft.Extensions.Logging;

namespace XivVault.Core.Backup;

public sealed record PortableFile(string ArchivePath, string SourcePath, long Size, DateTime LastWriteUtc, PortableItem Item);

public sealed record PortableSnapshot(
    string DataPath,
    IReadOnlyList<PortableFile> Files,
    IReadOnlyList<string> PluginNames,
    int PluginConfigDirectories)
{
    public long TotalBytes => Files.Sum(file => file.Size);

    public int PluginConfigCount => PluginNames.Count;

    public int CharacterCount => BackupAllowlist.CharacterCount(Files.Select(file => file.ArchivePath));

    public bool Has(PortableItem item) => Files.Any(file => file.Item == item);

    public DateTime? LastWriteUtc(PortableItem item)
    {
        var matches = Files.Where(file => file.Item == item).ToList();
        return matches.Count == 0 ? null : matches.Max(file => file.LastWriteUtc);
    }
}

/// <summary>
/// Lists the allowlisted files in a Dalamud data folder and, when given, the game's settings folder.
/// Reads metadata only, never contents.
/// </summary>
public sealed class PortableStateScanner(ILogger<PortableStateScanner> logger)
{
    public PortableSnapshot Scan(string dataPath, bool includeDalamudUi, string? gamePath = null)
    {
        var files = new List<PortableFile>();
        foreach (var item in new[] { PortableItem.DalamudConfig, PortableItem.DalamudVfs, PortableItem.DalamudUi })
        {
            if (item == PortableItem.DalamudUi && !includeDalamudUi)
            {
                continue;
            }

            var source = Path.Combine(dataPath, BackupAllowlist.FileNameFor(item));
            var info = new FileInfo(source);
            if (info.Exists && !IsLink(info))
            {
                files.Add(new PortableFile(BackupAllowlist.ArchivePathFor(item), info.FullName, info.Length, info.LastWriteTimeUtc, item));
            }
        }

        var pluginRoot = new DirectoryInfo(Path.Combine(dataPath, BackupAllowlist.PluginConfigsDirectory));
        var directories = 0;
        if (pluginRoot.Exists && !IsLink(pluginRoot))
        {
            directories = pluginRoot.EnumerateDirectories().Count(dir => !IsLink(dir) && !BackupAllowlist.IsExcludedPluginDirectory(dir.Name));
            Walk(pluginRoot, BackupAllowlist.PayloadPrefix + BackupAllowlist.PluginConfigsDirectory + "/", files);
        }

        if (gamePath is not null)
        {
            ScanGame(new DirectoryInfo(gamePath), files);
        }

        var pluginNames = BackupAllowlist.PluginNames(files.Select(file => file.ArchivePath));
        var snapshot = new PortableSnapshot(dataPath, files, pluginNames, directories);
        logger.LogInformation(
            "Scanned {Path}: {FileCount} files, {PluginCount} plugin configs, {CharacterCount} characters",
            dataPath,
            files.Count,
            pluginNames.Count,
            snapshot.CharacterCount);
        return snapshot;
    }

    /// <summary>
    /// The game folder itself is followed even when it is a link, as the game follows it. Links
    /// inside it are not, the same as in the XIVLauncher folder.
    /// </summary>
    private static void ScanGame(DirectoryInfo root, List<PortableFile> files)
    {
        if (!root.Exists)
        {
            return;
        }

        AddGameFiles(root, BackupAllowlist.GamePrefix, files);
        foreach (var character in root.EnumerateDirectories())
        {
            if (!IsLink(character) && BackupAllowlist.IsCharacterFolder(character.Name))
            {
                AddGameFiles(character, BackupAllowlist.GamePrefix + character.Name + "/", files);
            }
        }
    }

    private static void AddGameFiles(DirectoryInfo directory, string archivePrefix, List<PortableFile> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            var archivePath = archivePrefix + file.Name;
            if (!IsLink(file) && BackupAllowlist.TryClassify(archivePath, out var item, out _))
            {
                files.Add(new PortableFile(archivePath, file.FullName, file.Length, file.LastWriteTimeUtc, item));
            }
        }
    }

    private void Walk(DirectoryInfo directory, string archivePrefix, List<PortableFile> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            if (IsLink(file) || BackupAllowlist.IsExcludedPluginFile(file.Name))
            {
                continue;
            }

            var archivePath = archivePrefix + file.Name;
            if (!ArchivePaths.IsSafe(archivePath))
            {
                logger.LogWarning("Skipped a plugin config file whose name cannot be stored safely");
                continue;
            }

            files.Add(new PortableFile(archivePath, file.FullName, file.Length, file.LastWriteTimeUtc, PortableItem.PluginConfig));
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            // Links could point anywhere on the machine, so they are never followed.
            if (IsLink(child) || BackupAllowlist.IsExcludedPluginDirectory(child.Name))
            {
                continue;
            }

            Walk(child, archivePrefix + child.Name + "/", files);
        }
    }

    // Only symlinks and junctions count. Other reparse points (cloud placeholders, dedup) are real files.
    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null;
}
