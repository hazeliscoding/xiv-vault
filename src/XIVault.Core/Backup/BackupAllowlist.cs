namespace XIVault.Core.Backup;

public enum PortableItem
{
    PluginConfig,
    DalamudConfig,
    DalamudVfs,
    DalamudUi,
}

/// <summary>
/// The security and portability boundary: the only paths XIVault ever reads from XIVLauncher
/// or writes back to it. Everything else (installedPlugins, runtime, addon, logs, caches) is
/// machine-specific and stays out.
/// </summary>
public static class BackupAllowlist
{
    public const string ManifestEntryName = "manifest.json";
    public const string PayloadPrefix = "payload/";
    public const string PluginConfigsDirectory = "pluginConfigs";
    public const string DalamudConfigFile = "dalamudConfig.json";
    public const string DalamudVfsFile = "dalamudVfs.db";
    public const string DalamudUiFile = "dalamudUI.ini";

    private static readonly HashSet<string> ExcludedPluginDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "log", "cache", "caches", "temp", "tmp",
    };

    private static readonly string[] ExcludedPluginFileSuffixes =
    [
        ".tmp", ".temp", ".partial", ".bak~", ".log", "~",
    ];

    public static string ArchivePathFor(PortableItem item) => item switch
    {
        PortableItem.DalamudConfig => PayloadPrefix + DalamudConfigFile,
        PortableItem.DalamudVfs => PayloadPrefix + DalamudVfsFile,
        PortableItem.DalamudUi => PayloadPrefix + DalamudUiFile,
        _ => throw new ArgumentOutOfRangeException(nameof(item), "Plugin configs map to many paths."),
    };

    public static string FileNameFor(PortableItem item) => item switch
    {
        PortableItem.DalamudConfig => DalamudConfigFile,
        PortableItem.DalamudVfs => DalamudVfsFile,
        PortableItem.DalamudUi => DalamudUiFile,
        _ => throw new ArgumentOutOfRangeException(nameof(item), "Plugin configs map to many paths."),
    };

    /// <summary>
    /// Classifies an archive path. Returns false for anything outside the allowlist.
    /// <paramref name="relativeTarget"/> is the path relative to the Dalamud data folder, with the
    /// canonical spelling of the root files.
    /// </summary>
    public static bool TryClassify(string archivePath, out PortableItem item, out string relativeTarget)
    {
        item = default;
        relativeTarget = "";
        if (!archivePath.StartsWith(PayloadPrefix, StringComparison.Ordinal) || !ArchivePaths.IsSafe(archivePath))
        {
            return false;
        }

        var rest = archivePath[PayloadPrefix.Length..];
        foreach (var root in new[] { PortableItem.DalamudConfig, PortableItem.DalamudVfs, PortableItem.DalamudUi })
        {
            if (string.Equals(rest, FileNameFor(root), StringComparison.OrdinalIgnoreCase))
            {
                item = root;
                relativeTarget = FileNameFor(root);
                return true;
            }
        }

        var pluginPrefix = PluginConfigsDirectory + "/";
        if (!rest.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase) || rest.Length == pluginPrefix.Length)
        {
            return false;
        }

        // The same exclusions as the scanner: a restore must never write a file that the
        // pre-restore snapshot would not have saved first.
        var segments = rest[pluginPrefix.Length..].TrimEnd('/').Split('/');
        if (segments[..^1].Any(IsExcludedPluginDirectory) || IsExcludedPluginFile(segments[^1]))
        {
            return false;
        }

        item = PortableItem.PluginConfig;
        relativeTarget = PluginConfigsDirectory + "/" + rest[pluginPrefix.Length..];
        return true;
    }

    public static bool IsExcludedPluginDirectory(string name) => ExcludedPluginDirectories.Contains(name);

    public static bool IsExcludedPluginFile(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || ExcludedPluginFileSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The plugin a pluginConfigs entry belongs to: "Foo.json" and "Foo/..." are both plugin "Foo".</summary>
    public static string PluginNameFor(string pathUnderPluginConfigs)
    {
        var slash = pathUnderPluginConfigs.IndexOf('/');
        if (slash >= 0)
        {
            return pathUnderPluginConfigs[..slash];
        }

        var dot = pathUnderPluginConfigs.LastIndexOf('.');
        return dot > 0 ? pathUnderPluginConfigs[..dot] : pathUnderPluginConfigs;
    }

    /// <summary>Distinct plugin names in a set of archive paths, sorted for display.</summary>
    public static IReadOnlyList<string> PluginNames(IEnumerable<string> archivePaths)
    {
        var prefix = PayloadPrefix + PluginConfigsDirectory + "/";
        return archivePaths
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => PluginNameFor(path[prefix.Length..]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
