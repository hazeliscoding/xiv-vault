namespace XIVault.Core.Backup;

/// <summary>Rules for paths inside an archive, and for mapping them onto disk safely.</summary>
public static class ArchivePaths
{
    private const int MaxLength = 400;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidChars = ['<', '>', '"', '|', '?', '*', ':', '\\'];

    /// <summary>
    /// True for a relative, forward-slash path with no traversal and nothing Windows would reinterpret:
    /// no "..", no rooted or drive paths, no backslashes, no device names, no trailing dots or spaces.
    /// </summary>
    public static bool IsSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || path[0] == '/')
        {
            return false;
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            // A trailing slash marks a directory entry; every other segment must be a real name.
            if (segment.Length == 0)
            {
                if (i == segments.Length - 1 && i > 0)
                {
                    continue;
                }

                return false;
            }

            if (segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' ')
                || segment.IndexOfAny(InvalidChars) >= 0
                || segment.Any(char.IsControl)
                || ReservedNames.Contains(StripExtension(segment)))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsDirectoryEntry(string path) => path.EndsWith('/');

    /// <summary>
    /// Maps a relative archive path under <paramref name="root"/>, refusing anything that would land outside it.
    /// </summary>
    public static string ResolveUnder(string root, string relativePath)
    {
        if (!IsSafe(relativePath))
        {
            throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"Refusing unsafe archive path '{relativePath}'.");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new XivaultException(XivaultErrorKind.RestoreValidationFailed, $"Refusing archive path '{relativePath}' that leaves the target folder.");
        }

        return target;
    }

    private static string StripExtension(string segment)
    {
        var dot = segment.IndexOf('.');
        return dot < 0 ? segment : segment[..dot];
    }
}
