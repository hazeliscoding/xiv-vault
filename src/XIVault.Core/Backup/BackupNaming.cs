using System.Globalization;

namespace XIVault.Core.Backup;

public static class BackupNaming
{
    public const string RegularPrefix = "xivault-";
    public const string SafetyPrefix = "pre-restore-";
    public const string Extension = ".zip";
    public const string TempSuffix = ".tmp";

    /// <summary>A free file name for a new archive, such as xivault-2026-10-04-131900.zip. The name is for people; the manifest is authoritative.</summary>
    public static string NewFileName(string destination, BackupKind kind, DateTimeOffset localTime)
    {
        var prefix = kind == BackupKind.PreRestore ? SafetyPrefix : RegularPrefix;
        var stem = prefix + localTime.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var name = stem + Extension;
        for (var n = 2; Taken(destination, name); n++)
        {
            name = $"{stem}-{n}{Extension}";
        }

        return name;
    }

    /// <summary>File names XIVault itself creates. Used to flag our own archives whose manifest is unreadable.</summary>
    public static bool LooksLikeOurs(string fileName) =>
        fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && (fileName.StartsWith(RegularPrefix, StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase));

    private static bool Taken(string destination, string name) =>
        File.Exists(Path.Combine(destination, name)) || File.Exists(Path.Combine(destination, name + TempSuffix));
}
