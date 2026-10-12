using System.Text;
using XivVault.Core.Plugins;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>
/// What installedPlugins holds, judged as Dalamud judges it at its next start: it keeps only the
/// highest version folder and loads it only when it holds the DLL and a manifest.
/// </summary>
public class InstalledPluginScannerTests : IDisposable
{
    private static readonly Guid SplatoonId = Guid.Parse("7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e02");
    private static readonly Guid OtherId = Guid.Parse("b2a3c4d5-e6f7-4a8b-9c0d-1e2f3a4b5c03");

    private readonly TestEnvironment _env = new();

    private string Root => _env.XivLauncherPath;

    private string Plugins => Path.Combine(Root, "installedPlugins");

    public void Dispose() => _env.Dispose();

    private InstalledPlugins Scan() => InstalledPluginScanner.Scan(Root);

    private static string Manifest(Guid id, string scheduledForDeletion = "\"ScheduledForDeletion\": false") => $$"""
        {
          "Disabled": false,
          "Testing": false,
          {{scheduledForDeletion}},
          "InstalledFromUrl": "OFFICIAL",
          "WorkingPluginId": "{{id}}",
          "Name": "Splatoon",
          "InternalName": "Splatoon",
          "AssemblyVersion": "1.0.0.0",
          "DalamudApiLevel": 15
        }
        """;

    private static string WritePlugin(string pluginsFolder, string name, string version, string? manifest, bool dll = true)
    {
        var folder = Path.Combine(pluginsFolder, name, version);
        Directory.CreateDirectory(folder);
        if (dll)
        {
            File.WriteAllText(Path.Combine(folder, name + ".dll"), "not a real plugin");
        }

        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(folder, name + ".json"), manifest);
        }

        return folder;
    }

    private string WritePlugin(string name, string version, string? manifest, bool dll = true) => WritePlugin(Plugins, name, version, manifest, dll);

    [Fact]
    public void Nothing_is_installed_without_an_installedPlugins_folder()
    {
        var installed = Scan();

        Assert.Empty(installed.Folders);
        Assert.Empty(installed.Ids);
        Assert.False(installed.IsLink);
    }

    [Fact]
    public void Reads_an_installed_plugin_and_its_ID()
    {
        WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId));

        var installed = Scan();

        Assert.Equal(PluginFolderState.Installed, installed.StateOf("Splatoon"));
        Assert.Equal(PluginFolderState.Installed, installed.StateOf("SPLATOON"));
        Assert.Null(installed.StateOf("Artisan"));
        Assert.Equal([SplatoonId], installed.Ids);
        Assert.False(installed.IsLink);
    }

    [Fact]
    public void Leaves_out_empty_plugin_folders_and_stray_files()
    {
        Directory.CreateDirectory(Path.Combine(Plugins, "Splatoon"));
        File.WriteAllText(Path.Combine(Plugins, "Artisan"), "a file, not a plugin folder");

        var installed = Scan();

        Assert.Empty(installed.Folders);
        Assert.Null(installed.StateOf("Splatoon"));
    }

    [Fact]
    public void Uses_the_highest_version_folder()
    {
        WritePlugin("Splatoon", "9.0.0.0", Manifest(OtherId));
        WritePlugin("Splatoon", "10.0.0.0", Manifest(SplatoonId));
        WritePlugin("Splatoon", "latest", Manifest(OtherId));

        var installed = Scan();

        Assert.Equal(PluginFolderState.Installed, installed.StateOf("Splatoon"));
        Assert.Equal([SplatoonId], installed.Ids);
    }

    [Fact]
    public void A_lone_folder_is_installed_whatever_its_name()
    {
        // Dalamud deletes folders without a version name while it sorts the versions, and .NET doesn't sort just one.
        WritePlugin("Splatoon", "latest", Manifest(SplatoonId));

        var installed = Scan();

        Assert.Equal(PluginFolderState.Installed, installed.StateOf("Splatoon"));
        Assert.Equal([SplatoonId], installed.Ids);
    }

    [Fact]
    public void A_valid_lower_version_under_a_broken_highest_one_is_in_the_way()
    {
        // Dalamud deletes every lower version, then the highest one because it can't load it.
        WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId));
        WritePlugin("Splatoon", "1.1.0.0", manifest: null);

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Fact]
    public void A_plugin_marked_broken_is_in_the_way()
    {
        WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId));
        File.WriteAllText(Path.Combine(Plugins, "Splatoon", ".broken"), "");

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Theory]
    [InlineData("\"ScheduledForDeletion\": true")]
    [InlineData("\"scheduledForDeletion\": true")]
    [InlineData("\"ScheduledForDeletion\": null")]
    public void A_plugin_removed_in_the_game_is_in_the_way(string scheduledForDeletion)
    {
        WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId, scheduledForDeletion));

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Theory]
    [InlineData("no DLL")]
    [InlineData("no manifest")]
    [InlineData("an empty manifest")]
    [InlineData("a manifest that isn't JSON")]
    [InlineData("a null manifest")]
    [InlineData("a manifest that is a list")]
    [InlineData("an empty version folder")]
    [InlineData("a broken version folder beside one without a version name")]
    [InlineData("files only")]
    public void A_folder_Dalamud_would_not_load_is_in_the_way(string problem)
    {
        switch (problem)
        {
            case "no DLL":
                WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId), dll: false);
                break;
            case "no manifest":
                WritePlugin("Splatoon", "1.0.0.0", manifest: null);
                break;
            case "an empty manifest":
                WritePlugin("Splatoon", "1.0.0.0", "");
                break;
            case "a manifest that isn't JSON":
                WritePlugin("Splatoon", "1.0.0.0", "not json");
                break;
            case "a null manifest":
                WritePlugin("Splatoon", "1.0.0.0", "null");
                break;
            case "a manifest that is a list":
                WritePlugin("Splatoon", "1.0.0.0", "[]");
                break;
            case "an empty version folder":
                Directory.CreateDirectory(Path.Combine(Plugins, "Splatoon", "1.0.0.0"));
                break;
            case "a broken version folder beside one without a version name":
                WritePlugin("Splatoon", "latest", Manifest(SplatoonId));
                WritePlugin("Splatoon", "1.0.0.0", manifest: null);
                break;
            case "files only":
                Directory.CreateDirectory(Path.Combine(Plugins, "Splatoon"));
                File.WriteAllText(Path.Combine(Plugins, "Splatoon", "Splatoon.dll"), "not a real plugin");
                File.WriteAllText(Path.Combine(Plugins, "Splatoon", "Splatoon.json"), Manifest(SplatoonId));
                break;
        }

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Theory]
    [InlineData("""{ "InternalName": "Splatoon" }""")]
    [InlineData("""{ "WorkingPluginId": "00000000-0000-0000-0000-000000000000" }""")]
    public void An_installed_plugin_without_an_ID_adds_no_ID(string manifest)
    {
        WritePlugin("Splatoon", "1.0.0.0", manifest);

        var installed = Scan();

        Assert.Equal(PluginFolderState.Installed, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void An_ID_that_isn_t_a_GUID_is_in_the_way(string id)
    {
        // Dalamud reads the ID into a Guid, so the manifest fails to load and the plugin is skipped.
        WritePlugin("Splatoon", "1.0.0.0", $$"""{ "WorkingPluginId": {{id}} }""");

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
    }

    [Fact]
    public void A_manifest_that_isn_t_UTF8_is_in_the_way_and_the_rest_are_read()
    {
        // A bad byte in the ID, which is read; Dalamud can't read that ID either.
        WritePlugin("Splatoon", "1.0.0.0", Manifest(SplatoonId));
        var artisan = WritePlugin("Artisan", "1.0.0.0", manifest: null);
        File.WriteAllText(Path.Combine(artisan, "Artisan.json"), """{ "WorkingPluginId": "é" }""", Encoding.Latin1);

        var installed = Scan();

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Artisan"));
        Assert.Equal(PluginFolderState.Installed, installed.StateOf("Splatoon"));
        Assert.Equal([SplatoonId], installed.Ids);
    }

    [Fact]
    public void Reads_the_ID_without_case_as_Dalamud_does()
    {
        WritePlugin("Splatoon", "1.0.0.0", $$"""{ "workingPluginId": "{{SplatoonId}}" }""");

        Assert.Equal([SplatoonId], Scan().Ids);
    }

    [Fact]
    public void A_linked_installedPlugins_folder_installs_nothing()
    {
        var elsewhere = Path.Combine(_env.Root, "Elsewhere");
        WritePlugin(elsewhere, "Splatoon", "1.0.0.0", Manifest(SplatoonId));
        Directory.CreateDirectory(Root);
        RestoreHardeningTests.CreateJunction(Plugins, elsewhere);

        var installed = Scan();

        Assert.True(installed.IsLink);
        Assert.Empty(installed.Folders);
        Assert.Empty(installed.Ids);
    }

    [Fact]
    public void A_linked_plugin_folder_is_a_link()
    {
        var elsewhere = Path.Combine(_env.Root, "Elsewhere");
        WritePlugin(elsewhere, "Splatoon", "1.0.0.0", Manifest(SplatoonId));
        Directory.CreateDirectory(Plugins);
        RestoreHardeningTests.CreateJunction(Path.Combine(Plugins, "Splatoon"), Path.Combine(elsewhere, "Splatoon"));

        var installed = Scan();

        Assert.Equal(PluginFolderState.Link, installed.StateOf("Splatoon"));
        Assert.Empty(installed.Ids);
        Assert.False(installed.IsLink);
    }

    [Fact]
    public void A_link_to_an_empty_plugin_folder_is_a_link()
    {
        // An empty folder can be installed into, but never through a link.
        var elsewhere = Path.Combine(_env.Root, "Elsewhere", "Splatoon");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Plugins);
        RestoreHardeningTests.CreateJunction(Path.Combine(Plugins, "Splatoon"), elsewhere);

        Assert.Equal(PluginFolderState.Link, Scan().StateOf("Splatoon"));
    }

    [Fact]
    public void FakeXivLauncher_s_plugin_without_a_manifest_is_in_the_way()
    {
        var launcher = FakeXivLauncher.Create(Root);

        var installed = InstalledPluginScanner.Scan(launcher.Root);

        Assert.Equal(PluginFolderState.InTheWay, installed.StateOf("Artisan"));
        Assert.Single(installed.Folders);
        Assert.Empty(installed.Ids);
    }
}
