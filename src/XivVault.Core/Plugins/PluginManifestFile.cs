using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XivVault.Core.Plugins;

internal static class PluginManifestFile
{
    private const string MainRepository = "OFFICIAL";

    // Dalamud 15.0.3.6's LocalPluginManifest, in the order Newtonsoft writes it, with each property's default.
    // shortcut: one layout for every API level. Dalamud reads any valid manifest, so a newer Dalamud's
    // layout only matters for the byte-for-byte match with its own install; update this when it changes.
    private static readonly (string Name, Kind Kind, object? Default)[] Fields =
    [
        ("Disabled", Kind.Bool, false),
        ("Testing", Kind.Bool, false),
        ("ScheduledForDeletion", Kind.Bool, false),
        ("InstalledFromUrl", Kind.String, ""),
        ("WorkingPluginId", Kind.Guid, Guid.Empty),
        ("IsThirdParty", Kind.Computed, false),
        ("Author", Kind.String, null),
        ("Name", Kind.String, null),
        ("Punchline", Kind.String, null),
        ("Description", Kind.String, null),
        ("Changelog", Kind.String, null),
        ("Tags", Kind.Strings, null),
        ("CategoryTags", Kind.Strings, null),
        ("IsHide", Kind.Bool, false),
        ("InternalName", Kind.String, null),
        ("AssemblyVersion", Kind.Version, null),
        ("RepoUrl", Kind.String, null),
        ("ApplicableVersion", Kind.GameVersion, "any"),
        ("MinimumDalamudVersion", Kind.Version, null),
        ("DalamudApiLevel", Kind.Int, null),
        ("DownloadCount", Kind.Long, 0L),
        ("LastUpdate", Kind.Long, 0L),
        ("DownloadLinkInstall", Kind.String, null),
        ("DownloadLinkUpdate", Kind.String, null),
        ("DownloadLinkTesting", Kind.String, null),
        ("LoadRequiredState", Kind.Int, 0),
        ("LoadSync", Kind.Bool, false),
        ("LoadPriority", Kind.Int, 0),
        ("CanUnloadAsync", Kind.Bool, false),
        ("SupportsProfiles", Kind.Bool, true),
        ("ImageUrls", Kind.Strings, null),
        ("IconUrl", Kind.String, null),
        ("AcceptsFeedback", Kind.Bool, true),
        ("FeedbackMessage", Kind.String, null),
        ("_Dip17Channel", Kind.String, null),
        ("TestingAssemblyVersion", Kind.Version, null),
        ("TestingDalamudApiLevel", Kind.NullableInt, null),
        ("IsTestingExclusive", Kind.Bool, false),
    ];

    private enum Kind
    {
        Bool,
        Int,
        NullableInt,
        Long,
        String,
        Strings,
        Version,
        Guid,
        GameVersion,

        // IsThirdParty has no setter: Newtonsoft reads its value as a boolean, then drops it.
        Computed,
    }

    /// <summary>
    /// The bytes Dalamud's installer writes as a plugin's manifest, from the manifest packaged in the
    /// plugin, with the backup's ID instead of a new one.
    /// </summary>
    public static byte[] ForInstall(JsonElement packaged, Guid workingPluginId, bool testing, string installedFromUrl, string? dip17Channel, int apiLevel)
    {
        var values = Read(packaged, apiLevel);
        values["WorkingPluginId"] = workingPluginId;
        if (testing)
        {
            values["Testing"] = true;
        }

        values["InstalledFromUrl"] = installedFromUrl;
        values["_Dip17Channel"] = dip17Channel;
        values["IsThirdParty"] = installedFromUrl.Length > 0 && installedFromUrl != MainRepository;

        // Only plugins for any game version are installed, and Dalamud writes every spelling of "any" this way.
        values["ApplicableVersion"] = "any";
        return Write(values);
    }

    /// <summary>
    /// The packaged manifest's values as Newtonsoft deserializes Dalamud's LocalPluginManifest: unknown
    /// keys dropped, missing ones defaulted, and numbers and booleans also read from strings. Throws
    /// <see cref="InvalidDataException"/> for a value Newtonsoft can't convert, which fails Dalamud's install too.
    /// </summary>
    internal static Dictionary<string, object?> Read(JsonElement packaged, int apiLevel)
    {
        if (packaged.ValueKind != JsonValueKind.Object)
        {
            throw Unreadable();
        }

        var values = Fields.ToDictionary(field => field.Name, field => field.Default);
        values["DalamudApiLevel"] = apiLevel;
        foreach (var property in packaged.EnumerateObject())
        {
            // Newtonsoft matches a key exactly, then without case; no two of these names differ only by
            // case, so one lookup without case finds the same field. A repeated key is read again and wins.
            var index = Array.FindIndex(Fields, field => string.Equals(field.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            var (name, kind, _) = Fields[index];
            var value = ReadValue(property.Value, kind, values[name]);
            if (kind == Kind.Computed)
            {
                continue;
            }

            // A bool, int or long property can't hold null, so Newtonsoft fails on it.
            if (value is null && kind is Kind.Bool or Kind.Int or Kind.Long)
            {
                throw Unreadable();
            }

            values[name] = value;
        }

        return values;
    }

    private static object? ReadValue(JsonElement value, Kind kind, object? current) => kind switch
    {
        Kind.Bool or Kind.Computed => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.String when Text(value) is "" => null,
            JsonValueKind.String when bool.TryParse(Text(value), out var flag) => flag,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number != 0,
            _ => throw Unreadable(),
        },
        Kind.Int or Kind.NullableInt => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.Null => null,
            JsonValueKind.String when Text(value) is "" => null,
            JsonValueKind.String when int.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => throw Unreadable(),
        },

        // shortcut: a fractional number or a boolean for these repository counts is refused, though
        // Newtonsoft would convert it; convert it too if a plugin's manifest ever carries one.
        Kind.Long => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Null => null,
            JsonValueKind.String when long.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => throw Unreadable(),
        },
        Kind.Guid => value.ValueKind == JsonValueKind.String && Guid.TryParse(Text(value), out var id) ? id : throw Unreadable(),
        Kind.Version => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when Text(value) is "" => null,
            JsonValueKind.String when Version.TryParse(Text(value), out var version) => version,
            _ => throw Unreadable(),
        },

        // Newtonsoft adds a repeated list to the one it already read.
        Kind.Strings => value.ValueKind switch
        {
            JsonValueKind.Array => (current as List<string?> ?? []).Concat(value.EnumerateArray().Select(ReadString)).ToList(),
            JsonValueKind.Null => null,
            JsonValueKind.String when Text(value) is "" => null,
            _ => throw Unreadable(),
        },
        Kind.GameVersion => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => Text(value),
            _ => throw Unreadable(),
        },
        _ => ReadString(value),
    };

    private static string? ReadString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Text(value),
        JsonValueKind.Null => null,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw Unreadable(),
    };

    // shortcut: System.Text.Json can't read a lone surrogate escape, which Newtonsoft writes as U+FFFD,
    // so such a manifest is refused; unescape it by hand if a plugin's manifest ever carries one.
    private static string Text(JsonElement value)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw Unreadable();
        }
    }

    private static InvalidDataException Unreadable() => new("The plugin's manifest holds a value Dalamud can't read.");

    // Newtonsoft's Formatting.Indented, as Dalamud saves it: UTF-8 without a BOM, CRLF, no final newline.
    private static byte[] Write(Dictionary<string, object?> values)
    {
        var json = new StringBuilder("{");
        for (var i = 0; i < Fields.Length; i++)
        {
            json.Append(i == 0 ? "\r\n  \"" : ",\r\n  \"").Append(Fields[i].Name).Append("\": ");
            WriteValue(json, values[Fields[i].Name]);
        }

        return Encoding.UTF8.GetBytes(json.Append("\r\n}").ToString());
    }

    private static void WriteValue(StringBuilder json, object? value)
    {
        switch (value)
        {
            case null:
                json.Append("null");
                break;
            case bool flag:
                json.Append(flag ? "true" : "false");
                break;
            case int or long:
                json.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            case Guid id:
                json.Append('"').Append(id.ToString("D")).Append('"');
                break;
            case List<string?> { Count: 0 }:
                json.Append("[]");
                break;
            case List<string?> list:
                json.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    json.Append(i == 0 ? "\r\n    " : ",\r\n    ");
                    WriteValue(json, list[i]);
                }

                json.Append("\r\n  ]");
                break;
            default:
                WriteString(json, value.ToString()!);
                break;
        }
    }

    // Newtonsoft's default escaping. System.Text.Json's encoders escape more (NBSP, emoji, private use)
    // and in uppercase, so they can't produce the same bytes.
    private static void WriteString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case '\b':
                    json.Append("\\b");
                    break;
                case '\f':
                    json.Append("\\f");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                case < ' ' or '\u0085' or '\u2028' or '\u2029':
                    json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }
}
