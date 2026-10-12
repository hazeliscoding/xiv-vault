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

    public static int? CountCustomRepositories(Stream json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
    public static DalamudPluginSettings? ReadPluginSettings(Stream json) => throw new NotImplementedException();

    public static DalamudPluginSettings? ReadPluginSettings(string path) => throw new NotImplementedException();

    public static int? CountCustomRepositories(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return CountCustomRepositories(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
