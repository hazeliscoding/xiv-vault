using System.Reflection;
using System.Reflection.Emit;

namespace XivVault.Tests.Support;

/// <summary>What XIVLauncher's Dalamud download leaves in its folder: Dalamud itself and its ban list.</summary>
public static class FakeDalamud
{
    /// <summary>Writes addon\Hooks\&lt;folder&gt; as XIVLauncher prepares it, and returns that folder.</summary>
    public static string InstallDalamud(string rootPath, Version version, string? folder = null, DateTime? versionJsonWrittenUtc = null)
    {
        var hooks = Path.Combine(rootPath, "addon", "Hooks", folder ?? version.ToString());
        Directory.CreateDirectory(hooks);

        // A real assembly, so its version is read from metadata the way Dalamud's is.
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("Dalamud") { Version = version }, typeof(object).Assembly);
        assembly.DefineDynamicModule("Dalamud");
        assembly.Save(Path.Combine(hooks, "Dalamud.dll"));

        var versionJson = Path.Combine(hooks, "version.json");
        File.WriteAllText(versionJson, $$"""{"AssemblyVersion":"{{folder ?? version.ToString()}}","RuntimeVersion":"9.0.3","RuntimeRequired":true}""");
        if (versionJsonWrittenUtc is { } written)
        {
            File.SetLastWriteTimeUtc(versionJson, written);
        }

        return hooks;
    }

    public static void WriteBanList(string rootPath, string json, string assetVersion = "444")
    {
        var assets = Path.Combine(rootPath, "dalamudAssets");
        var uiRes = Path.Combine(assets, assetVersion, "UIRes");
        Directory.CreateDirectory(uiRes);
        File.WriteAllText(Path.Combine(assets, "asset.ver"), assetVersion);
        File.WriteAllText(Path.Combine(uiRes, "bannedplugin.json"), json);
    }
}
