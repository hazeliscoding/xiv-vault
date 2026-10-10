using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XivVault.Tests.Support;

/// <summary>Hand-builds archives, including hostile ones, to test validation and restore.</summary>
public sealed class ArchiveBuilder
{
    private readonly List<(string Name, byte[] Data)> _entries = [];
    private readonly List<JsonObject> _files = [];
    private string? _rawManifest;
    private int? _schemaVersion = 1;
    private JsonObject? _claimedContents;
    private int? _claimedCharacterCount;

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Adds a file to the archive and lists it in the manifest with its real hash.</summary>
    public ArchiveBuilder File(string name, string contents) => File(name, Encoding.UTF8.GetBytes(contents));

    public ArchiveBuilder File(string name, byte[] data)
    {
        _entries.Add((name, data));
        _files.Add(new JsonObject { ["path"] = name, ["size"] = data.Length, ["sha256"] = Sha256(data) });
        return this;
    }

    /// <summary>Adds an entry the manifest does not mention.</summary>
    public ArchiveBuilder UnlistedEntry(string name, string contents)
    {
        _entries.Add((name, Encoding.UTF8.GetBytes(contents)));
        return this;
    }

    /// <summary>Lists a file in the manifest with a hash that doesn't match its contents.</summary>
    public ArchiveBuilder FileWithWrongHash(string name, string contents)
    {
        var data = Encoding.UTF8.GetBytes(contents);
        _entries.Add((name, data));
        _files.Add(new JsonObject { ["path"] = name, ["size"] = data.Length, ["sha256"] = Sha256(Encoding.UTF8.GetBytes(contents + "!")) });
        return this;
    }

    public ArchiveBuilder SchemaVersion(int? version)
    {
        _schemaVersion = version;
        return this;
    }

    /// <summary>Makes the manifest claim contents that differ from its file list.</summary>
    public ArchiveBuilder ClaimContents(bool pluginConfigs, bool dalamudConfig, bool dalamudVfs, bool dalamudUi)
    {
        _claimedContents = new JsonObject { ["pluginConfigs"] = pluginConfigs, ["dalamudConfig"] = dalamudConfig, ["dalamudVfs"] = dalamudVfs, ["dalamudUi"] = dalamudUi };
        return this;
    }

    public ArchiveBuilder ClaimCharacterCount(int count)
    {
        _claimedCharacterCount = count;
        return this;
    }

    public ArchiveBuilder RawManifest(string json)
    {
        _rawManifest = json;
        return this;
    }

    public string Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in _entries)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(data);
        }

        using var manifest = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        manifest.Write(_rawManifest ?? BuildManifest());
        return path;
    }

    private List<string> Paths => _files.Select(file => (string)file["path"]!).ToList();

    // Counted here rather than by Core, so a mistake in Core's count can't hide in the test archives.
    private List<string> CharacterFolders => Paths
        .Select(path => path.Split('/'))
        .Where(segments => segments.Length == 4 && segments[1] == "game" && Regex.IsMatch(segments[2], "^FFXIV_CHR[0-9A-Fa-f]+$"))
        .Select(segments => segments[2])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private string BuildManifest()
    {
        var root = new JsonObject
        {
            ["xivVaultVersion"] = "0.1.0",
            ["createdAtUtc"] = "2026-09-28T18:38:00Z",
            ["backupType"] = "manual",
            ["source"] = new JsonObject { ["platform"] = "windows", ["layout"] = "standard" },
            ["contents"] = _claimedContents ?? new JsonObject
            {
                ["pluginConfigs"] = Paths.Any(path => path.StartsWith("payload/pluginConfigs/", StringComparison.OrdinalIgnoreCase)),
                ["dalamudConfig"] = Paths.Contains("payload/dalamudConfig.json"),
                ["dalamudVfs"] = Paths.Contains("payload/dalamudVfs.db"),
                ["dalamudUi"] = Paths.Contains("payload/dalamudUI.ini"),
                ["characterSettings"] = Paths.Any(path => path.StartsWith("payload/game/", StringComparison.OrdinalIgnoreCase) && path != "payload/game/FFXIV.cfg"),
                ["systemSettings"] = Paths.Contains("payload/game/FFXIV.cfg"),
            },
            ["statistics"] = new JsonObject
            {
                ["pluginConfigCount"] = XivVault.Core.Backup.BackupAllowlist.PluginNames(Paths).Count,
                ["pluginConfigDirectories"] = 0,
                ["characterCount"] = _claimedCharacterCount ?? CharacterFolders.Count,
                ["fileCount"] = _files.Count,
                ["totalBytes"] = _files.Sum(file => (int)file["size"]!),
            },
            ["files"] = new JsonArray([.. _files.Select(file => (JsonNode)file.DeepClone())]),
        };
        if (_schemaVersion is { } version)
        {
            root["schemaVersion"] = version;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
