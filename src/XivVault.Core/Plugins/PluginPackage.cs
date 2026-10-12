namespace XivVault.Core.Plugins;

internal static class PluginPackage
{
    /// <summary>
    /// Checks a downloaded plugin as Dalamud's installer does, and stricter, then extracts it into
    /// <paramref name="stagingFolder"/> with the manifest Dalamud would write, carrying the backup's ID.
    /// Returns <see cref="PluginInstallOutcome.Installed"/> when it is staged, or
    /// <see cref="PluginInstallOutcome.PackageRefused"/> after removing anything it wrote.
    /// </summary>
    public static PluginInstallOutcome Stage(string zipPath, PlannedInstall plan, Version dalamudVersion, string stagingFolder) =>
        throw new NotImplementedException();
}
