namespace XivVault.Core.Plugins;

/// <summary>The parts of XIVLauncher's Dalamud download that decide what can be installed.</summary>
internal static class DalamudInstall
{
    /// <summary>
    /// The version of the Dalamud XIVLauncher prepared last, read from <c>Dalamud.dll</c>'s metadata
    /// without loading it, or null when none is ready.
    /// </summary>
    public static Version? FindVersion(string rootPath) => throw new NotImplementedException();

    /// <summary>Dalamud's own copy of its ban list, or an empty list when there is none yet.</summary>
    public static DalamudBanList LoadBanList(string rootPath) => throw new NotImplementedException();
}
