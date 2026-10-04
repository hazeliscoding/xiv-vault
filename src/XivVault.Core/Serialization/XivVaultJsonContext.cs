using System.Text.Json.Serialization;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.State;

namespace XivVault.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(XivVaultConfig))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class XivVaultJsonContext : JsonSerializerContext;
