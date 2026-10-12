namespace XivVault.Core.Plugins;

internal static class InstallPlanner
{
    /// <summary>
    /// Decides, without any network or disk access, which plugins to install, from where and which
    /// build, and why each other plugin isn't installed.
    /// </summary>
    public static InstallPlan Plan(
        IReadOnlyList<PluginReference> plugins,
        InstalledPlugins installed,
        Version dalamudVersion,
        IReadOnlyList<RepositoryPlugin> official,
        IReadOnlyList<CustomRepositoryList> customRepositories,
        DalamudBanList bans,
        bool doPluginTest,
        IReadOnlySet<string> testingOptIns) => throw new NotImplementedException();
}
