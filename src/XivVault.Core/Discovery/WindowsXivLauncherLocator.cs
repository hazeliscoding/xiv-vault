using Microsoft.Extensions.Logging;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Platform;

namespace XivVault.Core.Discovery;

public interface IXivLauncherLocator
{
    /// <summary>
    /// Finds the XIVLauncher configuration folder. <paramref name="overridePath"/> wins over the
    /// configured override, which wins over detection. An override is never silently replaced by
    /// a detected folder.
    /// </summary>
    LocatorResult Locate(string? overridePath = null);
}

public sealed class WindowsXivLauncherLocator(
    IAppEnvironment environment,
    IConfigStore configStore,
    ILogger<WindowsXivLauncherLocator> logger) : IXivLauncherLocator
{
    public const string UserDataFolder = "dalamudUserData";

    // Things only XIVLauncher creates. A bare folder with none of these is not an installation.
    private static readonly string[] LauncherFileMarkers = ["launcherConfigV3.json", "launcherConfigV2.json", "launcherConfig.json"];
    private static readonly string[] LauncherDirectoryMarkers = ["addon", "runtime", "installedPlugins", "dalamudAssets"];

    public LocatorResult Locate(string? overridePath = null)
    {
        var explicitPath = overridePath;
        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            explicitPath = configStore.Load().XivLauncherPathOverride;
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Inspect(Path.GetFullPath(explicitPath), InstallationSource.Override, [explicitPath]);
        }

        var candidates = KnownPaths().ToList();
        LocatorResult? invalid = null;
        LocatorResult? launcherOnly = null;
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var result = Inspect(candidate, InstallationSource.KnownPath, candidates);
            if (result.IsFound && result.Installation!.HasPortableConfiguration)
            {
                return result;
            }

            if (result.IsFound)
            {
                launcherOnly ??= result;
            }
            else
            {
                invalid ??= result;
            }
        }

        return launcherOnly
            ?? invalid
            ?? new LocatorResult(
                LocatorStatus.NotFound,
                null,
                "XIVLauncher was not found. Install XIVLauncher and start it once, or set the folder in Settings.",
                candidates);
    }

    public IEnumerable<string> KnownPaths()
    {
        yield return Path.Combine(environment.RoamingAppData, "XIVLauncher");
        yield return Path.Combine(environment.RoamingAppData, "XIVLauncherCN");
    }

    private LocatorResult Inspect(string root, InstallationSource source, IReadOnlyList<string> checkedPaths)
    {
        if (!Directory.Exists(root))
        {
            return new LocatorResult(LocatorStatus.NotFound, null, $"The folder {root} does not exist.", checkedPaths);
        }

        var userData = Path.Combine(root, UserDataFolder);
        var rootArtifacts = ReadArtifacts(root);
        var userDataArtifacts = Directory.Exists(userData) ? ReadArtifacts(userData) : PortableArtifacts.None;

        var (layout, dataPath, artifacts) = userDataArtifacts.Any
            ? (PortableLayout.DalamudUserData, userData, userDataArtifacts)
            : (PortableLayout.Standard, root, rootArtifacts);

        if (!artifacts.Any && !HasLauncherMarkers(root))
        {
            logger.LogInformation("Rejected {Path}: no XIVLauncher or Dalamud files", root);
            return new LocatorResult(
                LocatorStatus.Invalid,
                null,
                $"{root} exists but has no XIVLauncher or Dalamud files.",
                checkedPaths);
        }

        var (executable, version) = FindLauncherExecutable();
        var installation = new XivLauncherInstallation(root, dataPath, layout, source, artifacts, executable, version);
        logger.LogInformation(
            "Found XIVLauncher at {Path} ({Layout}, portable configuration: {HasConfig})",
            root,
            layout,
            installation.HasPortableConfiguration);
        var message = installation.HasPortableConfiguration
            ? $"XIVLauncher found at {root}."
            : $"XIVLauncher found at {root}, but Dalamud has not saved any configuration there yet.";
        return new LocatorResult(LocatorStatus.Found, installation, message, checkedPaths);
    }

    public static PortableArtifacts ReadArtifacts(string dataPath) => new(
        Directory.Exists(Path.Combine(dataPath, BackupAllowlist.PluginConfigsDirectory)),
        File.Exists(Path.Combine(dataPath, BackupAllowlist.DalamudConfigFile)),
        File.Exists(Path.Combine(dataPath, BackupAllowlist.DalamudVfsFile)),
        File.Exists(Path.Combine(dataPath, BackupAllowlist.DalamudUiFile)));

    private static bool HasLauncherMarkers(string root) =>
        LauncherFileMarkers.Any(name => File.Exists(Path.Combine(root, name)))
        || LauncherDirectoryMarkers.Any(name => Directory.Exists(Path.Combine(root, name)));

    /// <summary>XIVLauncher installs (Squirrel) into %LOCALAPPDATA%\XIVLauncher with one app-x.y.z folder per version.</summary>
    private (string? Executable, string? Version) FindLauncherExecutable()
    {
        var installRoot = Path.Combine(environment.LocalAppData, "XIVLauncher");
        var stub = Path.Combine(installRoot, "XIVLauncher.exe");
        if (!File.Exists(stub))
        {
            return (null, null);
        }

        string? version = null;
        try
        {
            version = Directory.EnumerateDirectories(installRoot, "app-*")
                .Select(dir => Path.GetFileName(dir)["app-".Length..])
                .Select(text => Version.TryParse(text, out var parsed) ? parsed : null)
                .Where(parsed => parsed is not null)
                .Max()?.ToString();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return (stub, version);
    }
}
