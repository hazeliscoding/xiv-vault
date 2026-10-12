using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivVault.Core.Plugins;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

public class PluginPackageTests : IDisposable
{
    private const string Missing = "<missing>";

    private static readonly Version Dalamud = new(15, 0, 3, 6);
    private static readonly Guid PluginId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private readonly TestEnvironment _env = new();

    private string ZipPath => Path.Combine(_env.Root, "download", "TestPlugin.zip");

    private string Staging => Path.Combine(_env.Root, "work", "TestPlugin");

    public void Dispose() => _env.Dispose();

    private static PlannedInstall Plan(string version = FakePluginPackage.Version, string? repositoryUrl = null, bool useTesting = false, string internalName = FakePluginPackage.InternalName)
    {
        var entry = new RepositoryPlugin(
            internalName, Version.Parse(version), null, 15, null, false, false, null, true,
            "https://example.com/TestPlugin/latest.zip", null, "stable");
        return new PlannedInstall(new PluginReference(internalName, PluginId), entry, repositoryUrl, useTesting, Version.Parse(version), entry.DownloadLinkInstall!);
    }

    private PluginInstallOutcome Stage(FakePluginPackage package, PlannedInstall? plan = null, int maxEntries = PluginPackage.MaxEntries, long maxBytes = PluginPackage.MaxBytes) =>
        PluginPackage.Stage(package.Save(ZipPath), plan ?? Plan(), Dalamud, Staging, maxEntries, maxBytes);

    private void AssertRefused(FakePluginPackage package, PlannedInstall? plan = null, int maxEntries = PluginPackage.MaxEntries, long maxBytes = PluginPackage.MaxBytes)
    {
        Assert.Equal(PluginInstallOutcome.PackageRefused, Stage(package, plan, maxEntries, maxBytes));
        Assert.False(Path.Exists(Staging), "A refused package leaves nothing behind.");
    }

    private static FakePluginPackage WithManifestValue(string field, string json) =>
        FakePluginPackage.Typical(manifest =>
        {
            if (json == Missing)
            {
                manifest.Remove(field);
            }
            else
            {
                manifest[field] = JsonNode.Parse(json);
            }
        });

    private static byte[] ExpectedManifest(byte[] packaged, bool testing, string installedFromUrl)
    {
        using var document = JsonDocument.Parse(packaged);
        return PluginManifestFile.ForInstall(document.RootElement, PluginId, testing, installedFromUrl, "stable", 15);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://example.com/repo.json", true)]
    public void A_plugin_is_staged_file_for_file_with_the_manifest_Dalamud_would_write(string? repositoryUrl, bool useTesting)
    {
        var package = FakePluginPackage.Typical()
            .Entry("Assets/", [])
            .Entry("Assets/Images/icon.png", [0x89, 0x50, 0x4E, 0x47]);

        var outcome = Stage(package, Plan(repositoryUrl: repositoryUrl, useTesting: useTesting));

        Assert.Equal(PluginInstallOutcome.Installed, outcome);
        using var zip = ZipFile.OpenRead(ZipPath);
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        var staged = Directory.GetFiles(Staging, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Staging, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(files.Select(entry => entry.FullName).Order(StringComparer.Ordinal), staged);
        foreach (var entry in files)
        {
            using var packaged = new MemoryStream();
            using (var input = entry.Open())
            {
                input.CopyTo(packaged);
            }

            var path = Path.Combine(Staging, entry.FullName);
            if (entry.FullName == "TestPlugin.json")
            {
                Assert.Equal(ExpectedManifest(packaged.ToArray(), useTesting, repositoryUrl ?? "OFFICIAL"), File.ReadAllBytes(path));
            }
            else
            {
                Assert.Equal(packaged.ToArray(), File.ReadAllBytes(path));
                Assert.Equal(entry.LastWriteTime.UtcDateTime, File.GetLastWriteTimeUtc(path));
            }
        }
    }

    [Fact]
    public void The_manifest_and_DLL_are_found_without_case_as_Windows_finds_them()
    {
        var package = new FakePluginPackage()
            .Entry("testplugin.JSON", FakePluginPackage.TypicalManifest().ToJsonString())
            .Entry("TESTPLUGIN.dll", [0x4D, 0x5A]);

        Assert.Equal(PluginInstallOutcome.Installed, Stage(package));
        Assert.Equal(PluginId.ToString("D"), JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Staging, "testplugin.JSON"))).RootElement.GetProperty("WorkingPluginId").GetString());
    }

    [Fact]
    public void Backslashes_in_entry_names_are_folders_as_Windows_reads_them()
    {
        var package = FakePluginPackage.Typical().Entry("Assets\\Images\\icon.png", [0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal(PluginInstallOutcome.Installed, Stage(package));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(Path.Combine(Staging, "Assets", "Images", "icon.png")));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("Assets/../../evil.dll")]
    [InlineData("Assets\\..\\..\\evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("\\evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("C:\\evil.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("//server/share/evil.dll")]
    [InlineData("NUL.dll")]
    [InlineData("Assets/CON")]
    [InlineData("COM1.txt")]
    [InlineData("evil.dll:hidden")]
    [InlineData("evil.dll::$DATA")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("Assets./evil.dll")]
    [InlineData("control\u0001.dll")]
    [InlineData("new\nline.dll")]
    public void Unsafe_entry_paths_are_refused(string name)
    {
        AssertRefused(FakePluginPackage.Typical().Entry(name, "evil"));
    }

    [Theory]
    [InlineData("testplugin.DLL")]
    [InlineData("RUNTIMES/win-x64/native/Helper.dll")]
    [InlineData("de\\TestPlugin.resources.dll")]
    [InlineData("De")]
    [InlineData("de/TestPlugin.resources.dll/inner.txt")]
    public void An_entry_Windows_would_write_over_another_or_its_folder_is_refused(string name)
    {
        AssertRefused(FakePluginPackage.Typical().Entry(name, "clash"));
    }

    [Theory]
    [InlineData(0, PluginInstallOutcome.Installed)]
    [InlineData(-1, PluginInstallOutcome.PackageRefused)]
    public void The_entry_cap_counts_every_entry(int slack, PluginInstallOutcome expected)
    {
        var package = FakePluginPackage.Typical().Entry("Assets/", []);

        Assert.Equal(expected, Stage(package, maxEntries: package.EntryCount + slack));
        Assert.Equal(expected == PluginInstallOutcome.Installed, Path.Exists(Staging));
    }

    [Theory]
    [InlineData(0, PluginInstallOutcome.Installed)]
    [InlineData(-1, PluginInstallOutcome.PackageRefused)]
    public void The_size_cap_counts_the_bytes_actually_read_not_the_sizes_the_zip_records(int slack, PluginInstallOutcome expected)
    {
        var package = FakePluginPackage.Typical().Entry("Assets/big.bin", new byte[5000]).UnderstateSizes();

        Assert.Equal(expected, Stage(package, maxBytes: package.TotalBytes + slack));
        Assert.Equal(expected == PluginInstallOutcome.Installed, Path.Exists(Staging));
    }

    [Fact]
    public void A_package_without_its_DLL_is_refused()
    {
        AssertRefused(new FakePluginPackage().Manifest(FakePluginPackage.TypicalManifest()).Entry("Other.dll", [0x4D, 0x5A]));
    }

    [Fact]
    public void A_package_without_its_manifest_is_refused()
    {
        AssertRefused(new FakePluginPackage().Dll().Entry("Other.json", FakePluginPackage.TypicalManifest().ToJsonString()));
    }

    [Fact]
    public void A_manifest_and_DLL_outside_the_zip_root_are_refused()
    {
        AssertRefused(new FakePluginPackage()
            .Entry("TestPlugin/TestPlugin.json", FakePluginPackage.TypicalManifest().ToJsonString())
            .Entry("TestPlugin/TestPlugin.dll", [0x4D, 0x5A]));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"TestPlugin\"")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("not JSON")]
    public void A_manifest_that_is_not_a_JSON_object_is_refused(string manifest)
    {
        AssertRefused(new FakePluginPackage().Manifest(manifest).Dll());
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        var path = Path.Combine(_env.Root, "download", "TestPlugin.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html>Not found</html>");

        Assert.Equal(PluginInstallOutcome.PackageRefused, PluginPackage.Stage(path, Plan(), Dalamud, Staging));
        Assert.False(Path.Exists(Staging));
    }

    [Theory]
    [InlineData("InternalName", "\"OtherPlugin\"")]
    [InlineData("InternalName", "\"testplugin\"")]
    [InlineData("InternalName", Missing)]
    [InlineData("AssemblyVersion", "\"1.2.3.5\"")]
    [InlineData("AssemblyVersion", "\"1.2.3.4.5\"")]
    [InlineData("AssemblyVersion", Missing)]
    [InlineData("WorkingPluginId", "\"0b5e3f5e-8d0a-4c0e-9a51-2f4b7d1c9e10\"")]
    [InlineData("WorkingPluginId", "\"not an ID\"")]
    [InlineData("Name", "\"   \"")]
    [InlineData("Name", "null")]
    [InlineData("Name", Missing)]
    [InlineData("DalamudApiLevel", "14")]
    [InlineData("DalamudApiLevel", "\"16\"")]
    [InlineData("MinimumDalamudVersion", "\"15.0.3.7\"")]
    [InlineData("MinimumDalamudVersion", "\"16.0\"")]
    [InlineData("ApplicableVersion", "null")]
    [InlineData("ApplicableVersion", "\"2025.01.01.0000.0000\"")]
    [InlineData("LoadPriority", "\"high\"")]
    public void A_manifest_Dalamud_would_refuse_or_fail_to_load_is_refused(string field, string json)
    {
        AssertRefused(WithManifestValue(field, json));
    }

    [Theory]
    [InlineData("AssemblyVersion", "\"01.2.03.4\"")]
    [InlineData("WorkingPluginId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("DalamudApiLevel", Missing)]
    [InlineData("DalamudApiLevel", "\"15\"")]
    [InlineData("MinimumDalamudVersion", "\"15.0.3.6\"")]
    [InlineData("MinimumDalamudVersion", "\"15.0\"")]
    [InlineData("ApplicableVersion", Missing)]
    [InlineData("ApplicableVersion", "\"ANY\"")]
    public void Manifest_values_Dalamud_reads_the_same_way_are_staged(string field, string json)
    {
        Assert.Equal(PluginInstallOutcome.Installed, Stage(WithManifestValue(field, json)));
    }

    [Theory]
    [InlineData("4.6", PluginInstallOutcome.Installed)]
    [InlineData("4.6.0", PluginInstallOutcome.PackageRefused)]
    public void Versions_compare_as_Dalamud_compares_them(string packaged, PluginInstallOutcome expected)
    {
        var package = FakePluginPackage.Typical(manifest => manifest["AssemblyVersion"] = packaged);

        Assert.Equal(expected, Stage(package, Plan(version: "4.6")));
        Assert.Equal(expected == PluginInstallOutcome.Installed, Path.Exists(Staging));
    }

    [Fact]
    public void A_repository_name_with_a_folder_in_it_is_refused()
    {
        var manifest = FakePluginPackage.TypicalManifest();
        manifest["InternalName"] = "Sub/TestPlugin";
        var package = new FakePluginPackage()
            .Entry("Sub/TestPlugin.json", manifest.ToJsonString())
            .Entry("Sub/TestPlugin.dll", [0x4D, 0x5A]);

        AssertRefused(package, Plan(internalName: "Sub/TestPlugin"));
    }

    [Fact]
    public void An_existing_staging_folder_is_never_written_to_or_removed()
    {
        Directory.CreateDirectory(Staging);
        File.WriteAllText(Path.Combine(Staging, "keep.txt"), "not ours");

        Assert.Throws<IOException>(() => Stage(FakePluginPackage.Typical()));
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(Staging).Select(Path.GetFileName));
    }
}
