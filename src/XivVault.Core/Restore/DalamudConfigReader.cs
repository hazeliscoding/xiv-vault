using System.Text.Json;
using XivVault.Core.Plugins;

namespace XivVault.Core.Restore;

/// <summary>
/// Reads what restores need from dalamudConfig.json: how many custom plugin repositories it lists,
/// and the plugins to install. Nothing else is read, and nothing read here is ever logged.
/// </summary>
public static class DalamudConfigReader
{
    private const long MaxConfigBytes = 32 * 1024 * 1024;

    // Dalamud reads its files with Newtonsoft, which allows both.
    internal static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static int? CountCustomRepositories(Stream json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("ThirdRepoList"))
                {
                    continue;
                }

                // Dalamud saves with Newtonsoft type names, so lists may be wrapped as {"$type": ..., "$values": [...]}.
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$values", out var values))
                {
                    value = values;
                }

                return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
            }

            return 0;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The plugins to install and how to install them, or null when the file isn't a readable Dalamud
    /// configuration. Reads plugin names, IDs, custom repository URLs and testing choices, nothing else.
    /// </summary>
    public static DalamudPluginSettings? ReadPluginSettings(Stream json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Only the values: the keys are paths to DLLs on this PC.
            var devIds = TryGetProperty(root, "DevPluginSettings", out var dev) && dev.ValueKind == JsonValueKind.Object
                ? dev.EnumerateObject().Select(setting => ReadWorkingPluginId(setting.Value)).OfType<Guid>().ToHashSet()
                : [];

            var entries = new List<(string Name, Guid Id, bool InDefault)>();
            if (TryGetProperty(root, "DefaultProfile", out var defaultProfile))
            {
                AddPlugins(defaultProfile, inDefault: true, entries);
            }

            foreach (var profile in Items(root, "SavedProfiles"))
            {
                AddPlugins(profile, inDefault: false, entries);
            }

            entries.RemoveAll(entry => devIds.Contains(entry.Id));

            // An ID under two names doesn't say which plugin it belongs to.
            var shared = entries.GroupBy(entry => entry.Id)
                .Where(group => group.Any(entry => entry.Name != group.First().Name))
                .Select(group => group.Key)
                .ToHashSet();

            // The default collection's ID wins, then a name's last entry in the saved collections: Dalamud appends, so
            // after the goatcorp/Dalamud#1992 workaround the live ID comes last.
            var plugins = entries
                .Where(entry => !shared.Contains(entry.Id))
                .GroupBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(group => new PluginReference(group.Key, group.LastOrDefault(entry => entry.InDefault, group.Last()).Id))
                .ToList();

            // Dalamud fetches each enabled URL once, exactly as written.
            var repositories = Items(root, "ThirdRepoList")
                .Where(repository => TryGetProperty(repository, "IsEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                .Select(repository => ReadString(repository, "Url"))
                .OfType<string>()
                .Where(url => url.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var testingOptIns = Items(root, "PluginTestingOptIns")
                .Select(optIn => ReadString(optIn, "InternalName"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            var doPluginTest = TryGetProperty(root, "DoPluginTest", out var test) && test.ValueKind == JsonValueKind.True;
            return new DalamudPluginSettings(plugins, repositories, doPluginTest, testingOptIns);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DalamudPluginSettings? ReadPluginSettings(string path) => ReadFile(path, ReadPluginSettings);

    public static int? CountCustomRepositories(string path) => ReadFile(path, CountCustomRepositories);

    /// <summary>Finds a property as Newtonsoft, which Dalamud uses, matches it: the exact name first, then without case.</summary>
    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    internal static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A plugin's ID, or null when it has none: all zeros marks a plugin that hasn't been installed since.</summary>
    internal static Guid? ReadWorkingPluginId(JsonElement element) =>
        Guid.TryParse(ReadString(element, "WorkingPluginId"), out var id) && id != Guid.Empty ? id : null;

    // A collection also holds its own name and ID and the characters it is for, so only its plugins' names and IDs are read.
    private static void AddPlugins(JsonElement profile, bool inDefault, List<(string Name, Guid Id, bool InDefault)> entries)
    {
        foreach (var plugin in Items(profile, "Plugins"))
        {
            if (ReadString(plugin, "InternalName") is { Length: > 0 } name && ReadWorkingPluginId(plugin) is { } id)
            {
                entries.Add((name, id, inDefault));
            }
        }
    }

    // Dalamud saves with Newtonsoft type names, so lists may be wrapped as {"$type": ..., "$values": [...]}.
    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var list))
        {
            return [];
        }

        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("$values", out var values))
        {
            list = values;
        }

        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    }

    private static T? ReadFile<T>(string path, Func<Stream, T?> read)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes)
            {
                return default;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
