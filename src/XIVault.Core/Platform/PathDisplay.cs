namespace XIVault.Core.Platform;

/// <summary>Turns paths into something a person recognizes, and strips the user name from reports.</summary>
public sealed class PathDisplay(IAppEnvironment environment)
{
    /// <summary>"OneDrive / XIVault" or "%AppData%\XIVLauncher" instead of the full path.</summary>
    public string Friendly(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (environment.OneDrive is { } oneDrive && TryRelative(oneDrive, path, out var underOneDrive))
        {
            return Join("OneDrive", underOneDrive);
        }

        var dropbox = Path.Combine(environment.UserProfile, "Dropbox");
        if (TryRelative(dropbox, path, out var underDropbox))
        {
            return Join("Dropbox", underDropbox);
        }

        if (TryRelative(environment.RoamingAppData, path, out var underAppData))
        {
            return underAppData.Length == 0 ? "%AppData%" : @"%AppData%\" + underAppData;
        }

        return path;
    }

    /// <summary>Replaces the profile folder with %USERPROFILE% so a shared report doesn't carry the user name.</summary>
    public string Redact(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path ?? "";
        }

        return TryRelative(environment.UserProfile, path, out var relative)
            ? (relative.Length == 0 ? "%USERPROFILE%" : @"%USERPROFILE%\" + relative)
            : path;
    }

    /// <summary>
    /// Replaces the profile folder anywhere in free text, such as an error message that quotes a
    /// path, so nothing in a shared report carries the user name.
    /// </summary>
    public string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var profile = Path.TrimEndingDirectorySeparator(environment.UserProfile);
        var redacted = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return redacted.Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static string Join(string service, string relative) =>
        relative.Length == 0 ? service : service + " / " + relative.Replace('\\', '/').Replace("/", " / ");

    private static bool TryRelative(string root, string path, out string relative)
    {
        relative = "";
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                relative = fullPath[(fullRoot.Length + 1)..];
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        return false;
    }
}
