using System.Security.Cryptography;
using System.Text;

namespace XivVault.Core.Plugins;

/// <summary>A plugin Dalamud's settings list, with the ID its on/off states and collections use.</summary>
public sealed record PluginReference(string InternalName, Guid WorkingPluginId);

/// <summary>
/// What <c>dalamudConfig.json</c> says about plugins: the ones to install, the custom repositories
/// that are turned on, and which plugins use testing builds. Character entries, collection names
/// and dev plugin paths are never read.
/// </summary>
/// <param name="Plugins">One entry per plugin name, with the ID to install it under.</param>
/// <param name="CustomRepositories">Enabled custom repository URLs, exactly as written, without duplicates.</param>
/// <param name="DoPluginTest">Whether testing builds are turned on in Dalamud's settings.</param>
/// <param name="TestingOptIns">Plugins opted into testing builds, by internal name (ordinal).</param>
public sealed record DalamudPluginSettings(
    IReadOnlyList<PluginReference> Plugins,
    IReadOnlyList<string> CustomRepositories,
    bool DoPluginTest,
    IReadOnlySet<string> TestingOptIns);

public enum PluginFolderState
{
    /// <summary>A version folder holds the plugin's manifest.</summary>
    Installed,

    /// <summary>
    /// A copy Dalamud won't load is there: one without its DLL or manifest, one removed in the game, one
    /// marked broken, or one whose manifest Dalamud can't read. Dalamud removes all but the last at its
    /// next start; its own installer replaces any of them. XIV Vault never writes over one.
    /// </summary>
    InTheWay,

    /// <summary>The plugin's folder is a link to another location, which XIV Vault never writes through.</summary>
    Link,
}

/// <summary>What <c>installedPlugins</c> holds on this PC, read from folder names and manifests only.</summary>
/// <param name="Folders">Plugin folders that aren't empty, by name, matched without case as Windows matches them.</param>
/// <param name="Ids">The IDs in the installed manifests, so a backup's ID is never given to a second plugin.</param>
/// <param name="IsLink">True when <c>installedPlugins</c> itself is a link, so nothing is installed.</param>
public sealed record InstalledPlugins(IReadOnlyDictionary<string, PluginFolderState> Folders, IReadOnlySet<Guid> Ids, bool IsLink)
{
    public static InstalledPlugins None { get; } = new(new Dictionary<string, PluginFolderState>(StringComparer.OrdinalIgnoreCase), new HashSet<Guid>(), false);

    /// <summary>The state of the plugin's folder, or null when it is missing or empty and the plugin can be installed.</summary>
    public PluginFolderState? StateOf(string internalName) => Folders.TryGetValue(internalName, out var state) ? state : null;
}

/// <summary>Dalamud's list of plugin versions it refuses to load.</summary>
public sealed class DalamudBanList
{
    private readonly List<(string Name, Version? AssemblyVersion)> _entries;

    internal DalamudBanList(IEnumerable<(string Name, Version? AssemblyVersion)> entries) => _entries = entries.ToList();

    public static DalamudBanList Empty { get; } = new([]);

    /// <summary>
    /// Matches as Dalamud does: the entry names the plugin or the uppercase hex SHA-256 of its name,
    /// exactly, and bans every version up to its own, or every version when it lists none.
    /// </summary>
    public bool IsBanned(string internalName, Version version)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(internalName)));
        return _entries.Any(entry =>
            (string.Equals(entry.Name, internalName, StringComparison.Ordinal) || string.Equals(entry.Name, hash, StringComparison.Ordinal))
            && (entry.AssemblyVersion is null || entry.AssemblyVersion >= version));
    }
}

/// <summary>
/// One plugin as a repository lists it, reduced to what installing it needs. A null API level means
/// the repository left it out, which Dalamud reads as the current level. <c>AnyGameVersion</c> is
/// false for a plugin tied to one game version, and <c>Dip17Channel</c> is copied into the manifest
/// as the repository wrote it.
/// </summary>
public sealed record RepositoryPlugin(
    string InternalName,
    Version AssemblyVersion,
    Version? TestingAssemblyVersion,
    int? DalamudApiLevel,
    int? TestingDalamudApiLevel,
    bool IsTestingExclusive,
    bool IsHide,
    Version? MinimumDalamudVersion,
    bool AnyGameVersion,
    string? DownloadLinkInstall,
    string? DownloadLinkTesting,
    string? Dip17Channel);

/// <summary>A ticked custom repository and its plugins, or null plugins when it couldn't be fetched or read.</summary>
public sealed record CustomRepositoryList(string Url, IReadOnlyList<RepositoryPlugin>? Plugins);

/// <summary>
/// A plugin to download and install: the custom repository it comes from (null for the official
/// repository), whether Dalamud would install the testing build, and the version, which names its folder.
/// </summary>
public sealed record PlannedInstall(
    PluginReference Plugin,
    RepositoryPlugin Entry,
    string? RepositoryUrl,
    bool UseTesting,
    Version Version,
    string DownloadLink);

public enum PluginInstallOutcome
{
    Installed,
    AlreadyInstalled,
    InTheWay,
    IdInUse,
    NotOffered,
    RepositoryUnavailable,
    OfferedTwice,
    NotUpdated,
    NeedsNewerDalamud,
    NotForThisGameVersion,
    Banned,
    TestingOnly,
    DownloadFailed,
    PackageRefused,
    GameStarted,
    WriteFailed,
    DalamudNotReady,
    LinkedFolder,
}

public sealed record PluginOutcome(string InternalName, PluginInstallOutcome Outcome);

public sealed record InstallPlan(IReadOnlyList<PlannedInstall> Installs, IReadOnlyList<PluginOutcome> Skipped);

/// <summary>
/// Whether a restore can install plugins. Only beta versions can (the owner's call): a stable version
/// shows nothing about installs and never downloads a plugin. Tests and front ends can register their own.
/// </summary>
public sealed record PluginInstalls(bool Available)
{
    public static PluginInstalls ForThisVersion { get; } = new(XivVaultInfo.IsBeta);
}

/// <summary>
/// A host of custom repositories the backup's settings turn on, and how many. The host is shown instead of each URL,
/// which can hold a token, and in punycode, so a look-alike name can't pass for another.
/// </summary>
public sealed record CustomRepositoryHost(string Host, int Count);

/// <summary>What a restore would install, worked out without any request.</summary>
/// <param name="Missing">The backup's plugins that this PC doesn't have and nothing on it blocks, by name.</param>
/// <param name="CustomRepositories">The custom repositories the backup turns on, HTTPS only, by host for the user to tick.</param>
/// <param name="DalamudReady">False until XIVLauncher has finished downloading Dalamud; until then nothing is installed.</param>
public sealed record PluginInstallPreview(IReadOnlyList<string> Missing, IReadOnlyList<CustomRepositoryHost> CustomRepositories, bool DalamudReady);

/// <summary>What a restore installed. Every chosen plugin is counted once: installed, already installed, or not installed with its reason.</summary>
public sealed record PluginInstallReport(IReadOnlyList<string> Installed, IReadOnlyList<PluginOutcome> NotInstalled, int AlreadyInstalled);
