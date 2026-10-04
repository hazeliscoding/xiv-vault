namespace XivVault.Core.Discovery;

public enum PortableLayout
{
    /// <summary>Dalamud files sit directly in the XIVLauncher folder.</summary>
    Standard,

    /// <summary>Dalamud files sit in a <c>dalamudUserData</c> folder inside it.</summary>
    DalamudUserData,
}

public enum InstallationSource
{
    Override,
    KnownPath,
}

/// <summary>Which allowlisted items exist in the Dalamud data folder.</summary>
public sealed record PortableArtifacts(bool PluginConfigs, bool DalamudConfig, bool DalamudVfs, bool DalamudUi)
{
    public static readonly PortableArtifacts None = new(false, false, false, false);

    /// <summary>The UI layout alone doesn't count: it is optional and says nothing about Dalamud being set up.</summary>
    public bool Any => PluginConfigs || DalamudConfig || DalamudVfs;
}

public sealed record XivLauncherInstallation(
    string RootPath,
    string DataPath,
    PortableLayout Layout,
    InstallationSource Source,
    PortableArtifacts Artifacts,
    string? LauncherExecutable,
    string? LauncherVersion)
{
    /// <summary>
    /// False on a fresh PC where XIVLauncher ran but Dalamud never did. Such a folder can be
    /// restored into, but there is nothing to back up yet.
    /// </summary>
    public bool HasPortableConfiguration => Artifacts.Any;
}

public enum LocatorStatus
{
    Found,
    NotFound,

    /// <summary>A folder exists but holds nothing that proves it belongs to XIVLauncher.</summary>
    Invalid,
}

public sealed record LocatorResult(
    LocatorStatus Status,
    XivLauncherInstallation? Installation,
    string Message,
    IReadOnlyList<string> CheckedPaths)
{
    public bool IsFound => Status == LocatorStatus.Found && Installation is not null;
}
