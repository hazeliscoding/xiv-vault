using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XIVault.Tests.Support;

/// <summary>Hand-builds archives, including hostile ones, to test validation and restore.</summary>
public sealed class ArchiveBuilder
{
    private readonly List<(string Name, byte[] Data)> _entries = [];
    private readonly List<JsonObject> _files = [];
    private string? _rawManifest;
    private int? _schemaVersion = 1;

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

    private string BuildManifest()
    {
        var root = new JsonObject
        {
            ["xivaultVersion"] = "0.1.0",
            ["createdAtUtc"] = "2026-09-28T18:38:00Z",
            ["backupType"] = "manual",
            ["source"] = new JsonObject { ["platform"] = "windows", ["layout"] = "standard" },
            ["contents"] = new JsonObject { ["pluginConfigs"] = true, ["dalamudConfig"] = true, ["dalamudVfs"] = false, ["dalamudUi"] = false },
            ["statistics"] = new JsonObject { ["pluginConfigCount"] = 1, ["pluginConfigDirectories"] = 0, ["fileCount"] = _files.Count, ["totalBytes"] = 0 },
            ["files"] = new JsonArray([.. _files.Select(file => (JsonNode)file.DeepClone())]),
        };
        if (_schemaVersion is { } version)
        {
            root["schemaVersion"] = version;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
