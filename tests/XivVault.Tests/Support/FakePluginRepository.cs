using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivVault.Tests.Support;

/// <summary>
/// Plugin repositories on a fake web: the official list and custom lists as JSON, as Dalamud reads them,
/// and each plugin's zip at its download link.
/// </summary>
public sealed class FakePluginRepository
{
    public const string Official = "https://kamori.goats.dev/Plugin/PluginMaster";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly FakeWebDownloader _web;
    private readonly string _folder;
    private readonly Dictionary<string, JsonArray> _lists = new(StringComparer.Ordinal);
    private int _zips;

    public FakePluginRepository(FakeWebDownloader web, string folder)
    {
        _web = web;
        _folder = folder;
        Publish(Official, []);
    }

    /// <summary>A plugin's zip as Plogon packages it: its manifest and DLL at the root, and files in nested folders.</summary>
    public static FakePluginPackage Package(string name, string version = "1.0.0.0")
    {
        var manifest = FakePluginPackage.TypicalManifest();
        manifest["InternalName"] = name;
        manifest["Name"] = name + " (test)";
        manifest["AssemblyVersion"] = version;
        return new FakePluginPackage()
            .Entry(name + ".json", manifest.ToJsonString(Indented))
            .Entry(name + ".dll", [0x4D, 0x5A, .. Encoding.UTF8.GetBytes(name)])
            .Entry(name + ".deps.json", "{}")
            .Entry("runtimes/win-x64/native/helper.dll", [0x4D, 0x5A, 0x02, 0x00]);
    }

    /// <summary>
    /// Lists a plugin in a repository, the official one unless another is named, and serves its zip, which is
    /// <see cref="Package"/> unless one is given. Returns the zip's download link.
    /// </summary>
    public string Add(string name, string version = "1.0.0.0", string repository = Official, FakePluginPackage? package = null)
    {
        var link = repository == Official
            ? $"https://kamori.goats.dev/Plugin/Download/{Uri.EscapeDataString(name)}?isUpdate=False&isTesting=False&branch=api9&isDip17=True"
            : $"https://{new Uri(repository).Authority}/releases/{Uri.EscapeDataString(name)}/latest.zip";
        var entry = new JsonObject
        {
            ["Author"] = "Hazel",
            ["Name"] = name + " (test)",
            ["InternalName"] = name,
            ["AssemblyVersion"] = version,
            ["ApplicableVersion"] = "any",
            ["DalamudApiLevel"] = 15,

            // kamori hides most official entries, and Dalamud ignores the flag for them.
            ["IsHide"] = repository == Official,
            ["DownloadLinkInstall"] = link,
            ["DownloadLinkTesting"] = link + "&testing",
            ["_Dip17Channel"] = "stable",
        };
        Publish(repository, [entry]);

        var zip = (package ?? Package(name, version)).Save(Path.Combine(_folder, $"{++_zips}.zip"));
        _web.Bodies[link] = File.ReadAllBytes(zip);
        return link;
    }

    /// <summary>
    /// A dalamudConfig.json as Dalamud saves it, with Newtonsoft's type names: plugins in the default collection
    /// and in one saved collection, with the IDs their on/off states use, and custom repositories on or off.
    /// </summary>
    public static string DalamudConfig(
        IEnumerable<(string Name, Guid Id)> defaultCollection,
        IEnumerable<(string Name, Guid Id)>? savedCollection = null,
        IEnumerable<(string Url, bool Enabled)>? repositories = null)
    {
        static JsonObject TypedList(string type, IEnumerable<JsonNode> items) => new()
        {
            ["$type"] = $"System.Collections.Generic.List`1[[{type}, Dalamud]], System.Private.CoreLib",
            ["$values"] = new JsonArray([.. items]),
        };

        static JsonObject Collection(string name, IEnumerable<(string Name, Guid Id)> plugins) => new()
        {
            ["$type"] = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1, Dalamud",
            ["p"] = 0,
            ["e4c"] = false,
            ["pc"] = TypedList("Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Character", []),
            ["e"] = true,
            ["c"] = 0,
            ["Plugins"] = TypedList(
                "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin",
                plugins.Select(plugin => new JsonObject
                {
                    ["$type"] = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud",
                    ["InternalName"] = plugin.Name,
                    ["WorkingPluginId"] = plugin.Id.ToString(),
                    ["IsEnabled"] = true,
                })),
            ["id"] = Guid.NewGuid().ToString(),
            ["n"] = name,
        };

        var config = new JsonObject
        {
            ["$type"] = "Dalamud.Configuration.Internal.DalamudConfiguration, Dalamud",
            ["SavedProfiles"] = TypedList("Dalamud.Plugin.Internal.Profiles.ProfileModelV1", savedCollection is null ? [] : [Collection("Raid night", savedCollection)]),
            ["DefaultProfile"] = Collection("DEFAULT", defaultCollection),
            ["ThirdRepoList"] = TypedList(
                "Dalamud.Configuration.ThirdPartyRepoSettings",
                (repositories ?? []).Select(repository => new JsonObject
                {
                    ["$type"] = "Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud",
                    ["Url"] = repository.Url,
                    ["IsEnabled"] = repository.Enabled,
                })),
            ["DoPluginTest"] = false,
            ["LanguageOverride"] = "en",
        };
        return config.ToJsonString(Indented);
    }

    private void Publish(string repository, IEnumerable<JsonObject> entries)
    {
        if (!_lists.TryGetValue(repository, out var list))
        {
            _lists[repository] = list = [];
        }

        foreach (var entry in entries)
        {
            list.Add(entry);
        }

        _web.Bodies[repository] = Encoding.UTF8.GetBytes(list.ToJsonString(Indented));
    }
}
