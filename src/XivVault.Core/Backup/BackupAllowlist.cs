namespace XivVault.Core.Backup;

public enum PortableItem
{
    PluginConfig,
    DalamudConfig,
    DalamudVfs,
    DalamudUi,

    /// <summary>The game's shared macros, appearance saves and per-character settings.</summary>
    GameSettings,

    /// <summary><c>FFXIV.cfg</c>, the game's system settings.</summary>
    GameConfig,
}

/// <summary>
/// The security and portability boundary: the only paths XIV Vault ever reads from XIVLauncher or
/// the game's settings folder, or writes back to them. Everything else (installedPlugins, runtime,
/// addon, logs, caches, chat logs, screenshots) is machine-specific or private and stays out.
/// </summary>
public static class BackupAllowlist
{
    public const string ManifestEntryName = "manifest.json";
    public const string PayloadPrefix = "payload/";
    public const string PluginConfigsDirectory = "pluginConfigs";
    public const string DalamudConfigFile = "dalamudConfig.json";
    public const string DalamudVfsFile = "dalamudVfs.db";
    public const string DalamudUiFile = "dalamudUI.ini";
    public const string GameDirectory = "game";
    public const string GamePrefix = PayloadPrefix + GameDirectory + "/";
    public const string GameConfigFile = "FFXIV.cfg";
    public const string SharedMacrosFile = "MACROSYS.dat";
    public const string AppearanceSavePrefix = "FFXIV_CHARA_";
    public const string CharacterFolderPrefix = "FFXIV_CHR";

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
    /// <paramref name="relativeTarget"/> is the path relative to the Dalamud data folder, or to the
    /// game's settings folder for game items, with the canonical spelling of the fixed file names.
    /// </summary>
    public static bool TryClassify(string archivePath, out PortableItem item, out string relativeTarget)
    {
        item = default;
        relativeTarget = "";
        if (!archivePath.StartsWith(PayloadPrefix, StringComparison.Ordinal) || !ArchivePaths.IsSafe(archivePath))
        {
            return false;
        }

        if (archivePath.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return TryClassifyGame(archivePath[GamePrefix.Length..], out item, out relativeTarget);
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

    /// <summary>
    /// The game allowlist: <c>FFXIV.cfg</c>, <c>MACROSYS.dat</c>, the <c>FFXIV_CHARA_*.dat</c> appearance saves,
    /// and the <c>.DAT</c> files directly inside a character folder. Character files are matched by
    /// extension, so a file a game patch adds is not missed; chat logs, screenshots, <c>cfgcopy</c>,
    /// <c>*.old</c> and <c>FFXIV_BOOT.cfg</c> never match.
    /// </summary>
    private static bool TryClassifyGame(string relative, out PortableItem item, out string relativeTarget)
    {
        item = PortableItem.GameSettings;
        relativeTarget = relative;
        var segments = relative.Split('/');
        if (segments.Length == 2)
        {
            return IsCharacterFolder(segments[0]) && HasExtension(segments[1], ".dat");
        }

        if (segments.Length != 1)
        {
            return false;
        }

        var name = segments[0];
        if (string.Equals(name, GameConfigFile, StringComparison.OrdinalIgnoreCase))
        {
            item = PortableItem.GameConfig;
            relativeTarget = GameConfigFile;
            return true;
        }

        if (string.Equals(name, SharedMacrosFile, StringComparison.OrdinalIgnoreCase))
        {
            relativeTarget = SharedMacrosFile;
            return true;
        }

        return name.Length > AppearanceSavePrefix.Length + ".dat".Length
            && name.StartsWith(AppearanceSavePrefix, StringComparison.OrdinalIgnoreCase)
            && HasExtension(name, ".dat");
    }

    private static bool HasExtension(string name, string extension) =>
        string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsGame(PortableItem item) => item is PortableItem.GameSettings or PortableItem.GameConfig;

    /// <summary>A character folder is <c>FFXIV_CHR</c> and the character's content ID in hex.</summary>
    public static bool IsCharacterFolder(string name) =>
        name.Length > CharacterFolderPrefix.Length
        && name.StartsWith(CharacterFolderPrefix, StringComparison.OrdinalIgnoreCase)
        && name[CharacterFolderPrefix.Length..].All(char.IsAsciiHexDigit);

    /// <summary>Distinct character folders in a set of archive paths.</summary>
    public static int CharacterCount(IEnumerable<string> archivePaths) => archivePaths
        .Where(path => path.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase))
        .Select(path => path[GamePrefix.Length..].Split('/'))
        .Where(segments => segments.Length == 2 && IsCharacterFolder(segments[0]))
        .Select(segments => segments[0])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    /// <summary>
    /// A path that is safe to show or log. A character folder is named by a content ID that
    /// identifies the character, so it is never shown.
    /// </summary>
    public static string ForDisplay(string path) =>
        string.Join('/', path.Split('/').Select(segment => IsCharacterFolder(segment) ? CharacterFolderPrefix + "…" : segment));

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
