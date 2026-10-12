namespace XivVault.Core.Plugins;

internal static class PluginRepositoryParser
{
    /// <summary>
    /// The plugins a repository lists, leaving out entries Dalamud drops. Null when the repository as a
    /// whole can't be read, since Dalamud then lists nothing from it.
    /// </summary>
    public static IReadOnlyList<RepositoryPlugin>? Parse(Stream json) => throw new NotImplementedException();
}
