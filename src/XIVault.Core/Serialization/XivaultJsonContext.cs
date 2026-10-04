using System.Text.Json.Serialization;
using XIVault.Core.Backup;
using XIVault.Core.Configuration;
using XIVault.Core.State;

namespace XIVault.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(XivaultConfig))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class XivaultJsonContext : JsonSerializerContext;
