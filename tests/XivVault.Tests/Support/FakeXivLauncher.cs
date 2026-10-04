using System.Text;

namespace XivVault.Tests.Support;

/// <summary>Builds an XIVLauncher folder that looks like a real one, including the parts XIV Vault must ignore.</summary>
public sealed class FakeXivLauncher
{
    public static readonly string[] DefaultPlugins = ["Artisan", "AutoRetainer", "Pandora's Box", "Splatoon", "Waymark Preset Plugin"];

    private FakeXivLauncher(string root, string dataPath)
    {
        Root = root;
        DataPath = dataPath;
    }

    public string Root { get; }

    /// <summary>Where the Dalamud files live: the root, or root\dalamudUserData.</summary>
    public string DataPath { get; }

    public string PluginConfigs => Path.Combine(DataPath, "pluginConfigs");

    public static FakeXivLauncher Create(string root, bool userDataLayout = false, IEnumerable<string>? plugins = null, bool withUi = true)
    {
        var dataPath = userDataLayout ? Path.Combine(root, "dalamudUserData") : root;
        var fake = new FakeXivLauncher(root, dataPath);
        Directory.CreateDirectory(dataPath);

        File.WriteAllText(Path.Combine(root, "launcherConfigV3.json"), """{ "GamePath": "C:\\Games\\FFXIV" }""");
        File.WriteAllText(Path.Combine(dataPath, "dalamudConfig.json"), DalamudConfig(repos: 2));
        File.WriteAllBytes(Path.Combine(dataPath, "dalamudVfs.db"), Encoding.ASCII.GetBytes("SQLite format 3\0fake database body"));
        if (withUi)
        {
            File.WriteAllText(Path.Combine(dataPath, "dalamudUI.ini"), "[Window][Debug##Default]\nPos=60,60\n");
        }

        foreach (var plugin in plugins ?? DefaultPlugins)
        {
            fake.WritePluginConfig(plugin, $$"""{ "Version": 1, "Plugin": "{{plugin}}" }""");
        }

        // A plugin that keeps a folder of its own, with nested data and a cache XIV Vault must skip.
        var nested = Path.Combine(fake.PluginConfigs, "AutoRetainer", "profiles");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "main.json"), """{ "Retainers": 10 }""");
        Directory.CreateDirectory(Path.Combine(fake.PluginConfigs, "AutoRetainer", "cache"));
        File.WriteAllText(Path.Combine(fake.PluginConfigs, "AutoRetainer", "cache", "icons.bin"), "cache");
        File.WriteAllText(Path.Combine(fake.PluginConfigs, "Artisan.json.tmp"), "half-written");

        // Machine-specific things that must never leave this PC.
        Directory.CreateDirectory(Path.Combine(root, "installedPlugins", "Artisan", "1.0.0"));
        File.WriteAllText(Path.Combine(root, "installedPlugins", "Artisan", "1.0.0", "Artisan.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        File.WriteAllText(Path.Combine(root, "runtime", "coreclr.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(root, "addon", "Hooks"));
        File.WriteAllText(Path.Combine(root, "addon", "Hooks", "Dalamud.dll"), "binary");
        File.WriteAllText(Path.Combine(root, "dalamud.log"), "log line");
        File.WriteAllText(Path.Combine(root, "unrelated-file.txt"), "leave me alone");
        return fake;
    }

    public static string DalamudConfig(int repos)
    {
        var entries = string.Join(",", Enumerable.Range(1, repos).Select(i => $$"""{ "Url": "https://repo{{i}}.example.com/plugins.json", "IsEnabled": true }"""));
        return $$"""
            {
              "$type": "Dalamud.Configuration.Internal.DalamudConfiguration, Dalamud",
              "ThirdRepoList": { "$type": "System.Collections.Generic.List`1[[Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud]], System.Private.CoreLib", "$values": [{{entries}}] },
              "LanguageOverride": "en"
            }
            """;
    }

    public void WritePluginConfig(string plugin, string json)
    {
        Directory.CreateDirectory(PluginConfigs);
        File.WriteAllText(Path.Combine(PluginConfigs, plugin + ".json"), json);
    }

    public string ReadPluginConfig(string plugin) => File.ReadAllText(Path.Combine(PluginConfigs, plugin + ".json"));

    public string Read(params string[] relative) => File.ReadAllText(Path.Combine([DataPath, .. relative]));

    public void Write(string contents, params string[] relative) => File.WriteAllText(Path.Combine([DataPath, .. relative]), contents);
}
