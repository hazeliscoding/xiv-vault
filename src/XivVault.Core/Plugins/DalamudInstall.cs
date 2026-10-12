using System.Globalization;
using System.Reflection;
using System.Text.Json;
using XivVault.Core.Restore;

namespace XivVault.Core.Plugins;

/// <summary>The parts of XIVLauncher's Dalamud download that decide what can be installed.</summary>
internal static class DalamudInstall
{
    private const long MaxAssetVersionBytes = 64;
    private const long MaxBanListBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The version of the Dalamud XIVLauncher prepared last, read from <c>Dalamud.dll</c>'s metadata
    /// without loading it, or null when none is ready.
    /// </summary>
    public static Version? FindVersion(string rootPath)
    {
        try
        {
            var hooks = new DirectoryInfo(Path.Combine(rootPath, "addon", "Hooks"));
            if (!hooks.Exists)
            {
                return null;
            }

            // XIVLauncher writes version.json only after checking a download, so the newest one marks the Dalamud it
            // prepared last. Folder names and version.json can be git-describe strings, so only the DLL says the version.
            // "dev" is a copy of each download.
            var newest = hooks.EnumerateDirectories()
                .Where(folder => !folder.Name.Equals("dev", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(folder.FullName, "version.json"))
                    && File.Exists(Path.Combine(folder.FullName, "Dalamud.dll")))
                .MaxBy(folder => File.GetLastWriteTimeUtc(Path.Combine(folder.FullName, "version.json")));
            return newest is null ? null : AssemblyName.GetAssemblyName(Path.Combine(newest.FullName, "Dalamud.dll")).Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Dalamud's own copy of its ban list, or an empty list when there is none yet.</summary>
    public static DalamudBanList LoadBanList(string rootPath)
    {
        try
        {
            // XIVLauncher writes asset.ver as a number and names the assets folder after it, so anything else is
            // refused rather than followed out of dalamudAssets.
            var assets = Path.Combine(rootPath, "dalamudAssets");
            var assetVersionFile = new FileInfo(Path.Combine(assets, "asset.ver"));
            if (!assetVersionFile.Exists || assetVersionFile.Length > MaxAssetVersionBytes
                || !int.TryParse(File.ReadAllText(assetVersionFile.FullName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetVersion))
            {
                return DalamudBanList.Empty;
            }

            var list = new FileInfo(Path.Combine(assets, assetVersion.ToString(CultureInfo.InvariantCulture), "UIRes", "bannedplugin.json"));
            if (!list.Exists || list.Length > MaxBanListBytes)
            {
                return DalamudBanList.Empty;
            }

            using var stream = list.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, DalamudConfigReader.JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return DalamudBanList.Empty;
            }

            // A version that can't be read bans every version: the cautious reading of an entry Dalamud itself can't load.
            return new DalamudBanList(
                from entry in document.RootElement.EnumerateArray()
                let name = DalamudConfigReader.ReadString(entry, "Name")
                where name is not null
                select (name!, Version.TryParse(DalamudConfigReader.ReadString(entry, "AssemblyVersion"), out var version) ? version : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DalamudBanList.Empty;
        }
    }
}
