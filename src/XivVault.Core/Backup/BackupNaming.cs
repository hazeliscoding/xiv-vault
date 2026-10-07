using System.Globalization;

namespace XivVault.Core.Backup;

public static class BackupNaming
{
    public const string RegularPrefix = "xiv-vault-";
    public const string SafetyPrefix = "pre-restore-";
    public const string Extension = ".zip";
    public const string TempSuffix = ".tmp";

    private const string TimeFormat = "yyyy-MM-dd-HHmmss";

    /// <summary>A free file name for a new archive, such as xiv-vault-2026-10-04-131900.zip. The name is for people; the manifest is authoritative.</summary>
    public static string NewFileName(string destination, BackupKind kind, DateTimeOffset localTime)
    {
        var prefix = kind == BackupKind.PreRestore ? SafetyPrefix : RegularPrefix;
        var stem = prefix + localTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var name = stem + Extension;
        for (var n = 2; Taken(destination, name); n++)
        {
            name = $"{stem}-{n}{Extension}";
        }

        return name;
    }

    /// <summary>File names XIV Vault itself creates. Used to flag our own archives whose manifest is unreadable.</summary>
    public static bool LooksLikeOurs(string fileName) =>
        fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && (fileName.StartsWith(RegularPrefix, StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The local time in a name <see cref="NewFileName"/> made, or null for any other name.</summary>
    public static DateTime? LocalTimeFromName(string fileName)
    {
        var stem = fileName.StartsWith(RegularPrefix, StringComparison.OrdinalIgnoreCase) ? fileName[RegularPrefix.Length..]
            : fileName.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase) ? fileName[SafetyPrefix.Length..]
            : "";
        return stem.Length >= TimeFormat.Length
            && DateTime.TryParseExact(stem[..TimeFormat.Length], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                ? local
                : null;
    }

    private static bool Taken(string destination, string name) =>
        File.Exists(Path.Combine(destination, name)) || File.Exists(Path.Combine(destination, name + TempSuffix));
}
