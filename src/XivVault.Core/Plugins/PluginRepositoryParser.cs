using System.Globalization;
using System.Text.Json;

namespace XivVault.Core.Plugins;

/// <summary>
/// Reads a repository's list as Dalamud does, through Newtonsoft's defaults: comments, trailing commas,
/// keys in any case and loosely typed values are all accepted (PluginRepository.cs).
/// </summary>
internal static class PluginRepositoryParser
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// The plugins a repository lists, leaving out entries Dalamud drops. Null when the repository as a
    /// whole can't be read, since Dalamud then lists nothing from it.
    /// </summary>
    public static IReadOnlyList<RepositoryPlugin>? Parse(Stream json)
    {
        try
        {
            // Dalamud reads the body as text first, so a byte that isn't UTF-8 doesn't fail the list.
            using var reader = new StreamReader(json, leaveOpen: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), Options);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var plugins = new List<RepositoryPlugin>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                // Newtonsoft makes a manifest only from an object, and Dalamud fails on a null one.
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (Read(entry) is { } plugin)
                {
                    plugins.Add(plugin);
                }
            }

            return plugins;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RepositoryPlugin? Read(JsonElement entry)
    {
        // Every value is converted before an entry is dropped, because Newtonsoft reads the whole list first.
        // Dalamud's DalamudApiLevel isn't nullable: it may be left out, but an explicit null fails.
        var name = Field(entry, "Name", Text, null);
        var plugin = new RepositoryPlugin(
            Field(entry, "InternalName", Text, null)!,
            Field(entry, "AssemblyVersion", VersionOf, null)!,
            Field(entry, "TestingAssemblyVersion", VersionOf, null),
            Field<int?>(entry, "DalamudApiLevel", value => Integer(value) ?? throw new FormatException(), null),
            Field(entry, "TestingDalamudApiLevel", Integer, null),
            Field(entry, "IsTestingExclusive", Flag, false),
            Field(entry, "IsHide", Flag, false),
            Field(entry, "MinimumDalamudVersion", VersionOf, null),
            Field(entry, "ApplicableVersion", IsAnyGameVersion, true),
            Field(entry, "DownloadLinkInstall", Text, null),
            Field(entry, "DownloadLinkTesting", Text, null),
            Field(entry, "_Dip17Channel", Text, null));

        // PluginRepository.cs L181-210.
        return string.IsNullOrWhiteSpace(plugin.InternalName) || string.IsNullOrWhiteSpace(name) || plugin.AssemblyVersion is null ? null : plugin;
    }

    // Newtonsoft matches a key without case when none matches exactly, and sets the field once for each
    // matching key, so every value must convert and the last one wins.
    private static T Field<T>(JsonElement entry, string name, Func<JsonElement, T> convert, T missing)
    {
        var value = missing;
        foreach (var property in entry.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = convert(property.Value);
            }
        }

        return value;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => null,
        _ => throw new FormatException(),
    };

    // Penumbra's repository sends "IsHide": "False", and Newtonsoft reads any number but zero as true.
    private static bool Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var flag) => flag,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => throw new FormatException(),
    };

    // Newtonsoft reads an empty string as no value for numbers and versions.
    private static int? Integer(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when value.GetString() is "" => null,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        JsonValueKind.Null => null,
        _ => throw new FormatException(),
    };

    private static Version? VersionOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is "" => null,
        JsonValueKind.String when Version.TryParse(value.GetString(), out var version) => version,
        JsonValueKind.Null => null,
        _ => throw new FormatException(),
    };

    // Dalamud's GameVersion is "any" in any case or up to five dot-separated whole numbers. An explicit
    // null makes Dalamud's load throw, so it counts as tied to a game version.
    private static bool IsAnyGameVersion(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.String when string.Equals(value.GetString(), "any", StringComparison.OrdinalIgnoreCase) => true,
        JsonValueKind.String when IsGameVersion(value.GetString()!) => false,
        _ => throw new FormatException(),
    };

    private static bool IsGameVersion(string text) =>
        text.Split('.') is { Length: <= 5 } parts
        && parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0);
}
