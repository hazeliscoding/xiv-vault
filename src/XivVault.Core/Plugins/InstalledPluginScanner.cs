using System.Text.Json;
using XivVault.Core.Restore;

namespace XivVault.Core.Plugins;

internal static class InstalledPluginScanner
{
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>
    /// Reads what <c>installedPlugins</c> under <paramref name="rootPath"/> holds. Never loads or runs anything.
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the folder can't be listed.
    /// </summary>
    public static InstalledPlugins Scan(string rootPath)
    {
        var root = new DirectoryInfo(Path.Combine(rootPath, "installedPlugins"));
        if (root.LinkTarget is not null)
        {
            return InstalledPlugins.None with { IsLink = true };
        }

        if (!root.Exists)
        {
            return InstalledPlugins.None;
        }

        var folders = new Dictionary<string, PluginFolderState>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        foreach (var folder in root.EnumerateDirectories())
        {
            var (state, id) = Classify(folder);
            if (state is { } known)
            {
                folders[folder.Name] = known;
            }

            if (id is { } loaded && loaded != Guid.Empty)
            {
                ids.Add(loaded);
            }
        }

        return new InstalledPlugins(folders, ids, IsLink: false);
    }

    // As Dalamud decides at its next start (PluginManager.CleanupPlugins, then LoadAllPlugins): a ".broken" marker
    // deletes the plugin, and only the highest version folder is kept. Folders without a version name are deleted while
    // they are sorted, and .NET doesn't sort a lone folder, so that one is kept whatever its name. The kept folder loads
    // only when it holds <Name>.dll and a non-empty <Name>.json that isn't scheduled for deletion. Otherwise Dalamud
    // deletes or ignores it.
    private static (PluginFolderState? State, Guid? Id) Classify(DirectoryInfo folder)
    {
        try
        {
            if (folder.LinkTarget is not null)
            {
                return (PluginFolderState.Link, null);
            }

            // An uninstall can leave an empty folder, and installing into it is fine.
            if (!folder.EnumerateFileSystemInfos().Any())
            {
                return (null, null);
            }

            var versions = File.Exists(Path.Combine(folder.FullName, ".broken")) ? [] : folder.GetDirectories();
            var kept = versions.Length == 1
                ? versions[0]
                : versions.Where(version => Version.TryParse(version.Name, out _)).MaxBy(version => Version.Parse(version.Name));
            var id = kept is null ? null : LoadableId(kept, folder.Name);
            return id is null ? (PluginFolderState.InTheWay, null) : (PluginFolderState.Installed, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // InvalidOperationException is a string in the manifest that isn't UTF-8.
            return (PluginFolderState.InTheWay, null);
        }
    }

    /// <summary>
    /// Null when Dalamud wouldn't load the version folder; otherwise the manifest's ID, or <see cref="Guid.Empty"/>
    /// when it has none and Dalamud gives it a new one. Only checks that the DLL exists.
    /// </summary>
    private static Guid? LoadableId(DirectoryInfo versionFolder, string name)
    {
        var manifest = new FileInfo(Path.Combine(versionFolder.FullName, name + ".json"));
        if (!File.Exists(Path.Combine(versionFolder.FullName, name + ".dll")) || !manifest.Exists || manifest.Length is 0 or > MaxManifestBytes)
        {
            return null;
        }

        using var stream = manifest.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream, DalamudConfigReader.JsonOptions);
        var root = document.RootElement;

        // True schedules the deletion. Dalamud only ever writes a boolean here, so any other value is read as gone too.
        if (root.ValueKind != JsonValueKind.Object
            || (DalamudConfigReader.TryGetProperty(root, "ScheduledForDeletion", out var scheduled) && scheduled.ValueKind != JsonValueKind.False))
        {
            return null;
        }

        if (!DalamudConfigReader.TryGetProperty(root, "WorkingPluginId", out var workingPluginId))
        {
            return Guid.Empty;
        }

        // Dalamud reads the ID into a Guid, so any other value fails the whole manifest and the plugin isn't loaded.
        return workingPluginId.ValueKind == JsonValueKind.String && Guid.TryParse(workingPluginId.GetString(), out var id) ? id : null;
    }
}
