using System.Text.Json;

namespace XivVault.Core.Plugins;

internal static class PluginManifestFile
{
    /// <summary>
    /// The bytes Dalamud's installer writes as a plugin's manifest, from the manifest packaged in the
    /// plugin, with the backup's ID instead of a new one.
    /// </summary>
    public static byte[] ForInstall(JsonElement packaged, Guid workingPluginId, bool testing, string installedFromUrl, string? dip17Channel, int apiLevel) =>
        throw new NotImplementedException();
}
