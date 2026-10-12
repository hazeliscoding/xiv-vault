using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivVault.Tests.Support;

/// <summary>
/// Builds plugin zips as repositories serve them, and hostile ones: any entry name, and sizes the zip
/// understates so a reader that trusts them would read more than it expects.
/// </summary>
public sealed class FakePluginPackage
{
    public const string InternalName = "TestPlugin";

    public const string Version = "1.2.3.4";

    /// <summary>Old enough that a file stamped with the time it was written can't match by chance.</summary>
    public static readonly DateTimeOffset EntryTime = new(2025, 3, 14, 15, 9, 26, TimeSpan.Zero);

    private readonly List<(string Name, byte[] Data)> _entries = [];
    private bool _understateSizes;

    /// <summary>The fields Plogon packages for a typical plugin.</summary>
    public static JsonObject TypicalManifest() => new()
    {
        ["Author"] = "Hazel",
        ["Name"] = "Test Plugin",
        ["InternalName"] = InternalName,
        ["AssemblyVersion"] = Version,
        ["Description"] = "Does one thing.\nThen another.",
        ["ApplicableVersion"] = "any",
        ["RepoUrl"] = "https://github.com/example/TestPlugin",
        ["Tags"] = new JsonArray("Utility", "UI"),
        ["DalamudApiLevel"] = 15,
        ["LoadRequiredState"] = 0,
        ["LoadSync"] = false,
        ["CanUnloadAsync"] = false,
        ["LoadPriority"] = 0,
        ["Punchline"] = "A plugin for tests.",
        ["AcceptsFeedback"] = true,
    };

    /// <summary>A typical plugin: its manifest, its DLL, and files in nested folders.</summary>
    public static FakePluginPackage Typical(Action<JsonObject>? editManifest = null)
    {
        var manifest = TypicalManifest();
        editManifest?.Invoke(manifest);
        return new FakePluginPackage()
            .Manifest(manifest)
            .Dll()
            .Entry("TestPlugin.deps.json", "{}")
            .Entry("de/TestPlugin.resources.dll", [0x4D, 0x5A, 0x01])
            .Entry("runtimes/win-x64/native/helper.dll", [0x4D, 0x5A, 0x02, 0x00]);
    }

    public int EntryCount => _entries.Count;

    public long TotalBytes => _entries.Sum(entry => (long)entry.Data.Length);

    public FakePluginPackage Manifest(JsonObject fields) => Manifest(fields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The packaged manifest exactly as written, valid JSON or not.</summary>
    public FakePluginPackage Manifest(string rawJson) => Entry($"{InternalName}.json", rawJson);

    public FakePluginPackage Dll() => Entry($"{InternalName}.dll", [0x4D, 0x5A, 0x90, 0x00, 0x03]);

    /// <summary>Adds an entry under any name: nested folders, a directory entry ending in a slash, or an attack.</summary>
    public FakePluginPackage Entry(string name, string text) => Entry(name, Encoding.UTF8.GetBytes(text));

    public FakePluginPackage Entry(string name, byte[] data)
    {
        _entries.Add((name, data));
        return this;
    }

    /// <summary>Records every entry as one byte long. Entries are stored uncompressed, so readers still get all the data.</summary>
    public FakePluginPackage UnderstateSizes()
    {
        _understateSizes = true;
        return this;
    }

    public string Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in _entries)
            {
                var entry = zip.CreateEntry(name, _understateSizes ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = EntryTime;
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        var bytes = memory.ToArray();
        if (_understateSizes)
        {
            Understate(bytes);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Rewrites the uncompressed size in each central directory record and in the local header it points to.
    private static void Understate(byte[] zip)
    {
        var end = zip.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)) != 0x06054b50)
        {
            end--;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var record = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(record + 24), 1);
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(record + 42));
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22), 1);
            record += 46
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(record + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(record + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(record + 32));
        }
    }
}
