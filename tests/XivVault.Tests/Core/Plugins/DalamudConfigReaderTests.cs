using System.Text;
using XivVault.Core.Plugins;
using XivVault.Core.Restore;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>
/// Which plugins a restored dalamudConfig.json asks for. Dalamud saves it with Newtonsoft type names,
/// and its collections hold characters, which are never read.
/// </summary>
public class DalamudConfigReaderTests
{
    private static readonly Guid ArtisanId = Guid.Parse("0f3c6a52-5d0e-4c3b-9a35-1d2e7b8c9a01");
    private static readonly Guid SplatoonId = Guid.Parse("7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e02");
    private static readonly Guid AutoRetainerId = Guid.Parse("b2a3c4d5-e6f7-4a8b-9c0d-1e2f3a4b5c03");

    private static DalamudPluginSettings? Read(string json) =>
        DalamudConfigReader.ReadPluginSettings(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static string Plugin(string name, Guid id) =>
        $$"""{ "$type": "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud", "InternalName": "{{name}}", "WorkingPluginId": "{{id}}", "IsEnabled": true }""";

    /// <summary>A list as Newtonsoft saves it with type names, or as a bare array.</summary>
    private static string List(bool typeNames, params IEnumerable<string> items)
    {
        var array = "[" + string.Join(", ", items) + "]";
        return typeNames ? $$"""{ "$type": "System.Collections.Generic.List`1[[System.Object, System.Private.CoreLib]], System.Private.CoreLib", "$values": {{array}} }""" : array;
    }

    private static string Collection(bool typeNames, params IEnumerable<string> plugins) => $$"""
        {
          "$type": "Dalamud.Plugin.Internal.Profiles.ProfileModelV1, Dalamud",
          "p": 0,
          "e4c": false,
          "pc": {{List(typeNames)}},
          "e": true,
          "c": 0,
          "Plugins": {{List(typeNames, plugins)}},
          "id": "{{Guid.NewGuid()}}",
          "n": "A collection"
        }
        """;

    // Dalamud writes SavedProfiles before DefaultProfile.
    private static string Config(string? savedProfiles = null, string? defaultProfile = null) => $$"""
        {
          "$type": "Dalamud.Configuration.Internal.DalamudConfiguration, Dalamud",
          "SavedProfiles": {{savedProfiles ?? "null"}},
          "DefaultProfile": {{defaultProfile ?? "null"}},
          "ProfilesEnabled": true
        }
        """;

    private static string Describe(DalamudPluginSettings settings) => string.Join(
        "\n",
        [
            .. settings.Plugins.Select(plugin => $"{plugin.InternalName} {plugin.WorkingPluginId}"),
            .. settings.CustomRepositories,
            .. settings.TestingOptIns,
            settings.DoPluginTest.ToString(),
        ]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_the_plugins_of_the_default_and_saved_collections(bool typeNames)
    {
        var settings = Read(Config(
            savedProfiles: List(typeNames, Collection(typeNames, Plugin("AutoRetainer", AutoRetainerId)), Collection(typeNames, Plugin("Artisan", ArtisanId))),
            defaultProfile: Collection(typeNames, Plugin("Artisan", ArtisanId), Plugin("Splatoon", SplatoonId))));

        Assert.NotNull(settings);
        PluginReference[] expected = [new("Artisan", ArtisanId), new("Splatoon", SplatoonId), new("AutoRetainer", AutoRetainerId)];
        Assert.Equal(expected, settings.Plugins);
    }

    [Fact]
    public void Never_reads_a_collection_s_characters_name_or_ID()
    {
        const string CharacterName = "Wren Ashgrove";
        const string ContentId = "18014498565293837";
        const string World = "Gilgamesh";
        const string CollectionName = "Raid night with Wren";
        var collectionId = Guid.Parse("d4c3b2a1-0f9e-4d8c-b7a6-958473625140");
        var json = $$"""
            {
              "SavedProfiles": [
                {
                  "p": 0,
                  "e4c": true,
                  "pc": [ { "d": "{{CharacterName}}", "c": {{ContentId}}, "s": "{{World}}" } ],
                  "e": true,
                  "c": 4278190335,
                  "Plugins": [ {{Plugin("Splatoon", SplatoonId)}} ],
                  "id": "{{collectionId}}",
                  "n": "{{CollectionName}}"
                }
              ]
            }
            """;

        var settings = Read(json);

        Assert.NotNull(settings);
        Assert.Equal([new PluginReference("Splatoon", SplatoonId)], settings.Plugins);
        var everything = Describe(settings);
        foreach (var secret in new[] { CharacterName, ContentId, World, CollectionName, collectionId.ToString() })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_config_without_collections_lists_no_plugins()
    {
        var settings = Read(FakeXivLauncher.DalamudConfig(repos: 2));

        Assert.NotNull(settings);
        Assert.Empty(settings.Plugins);
        Assert.Equal(["https://repo1.example.com/plugins.json", "https://repo2.example.com/plugins.json"], settings.CustomRepositories);
        Assert.False(settings.DoPluginTest);
        Assert.Empty(settings.TestingOptIns);
    }

    [Fact]
    public void Drops_entries_without_a_usable_name_or_ID()
    {
        var json = Config(defaultProfile: Collection(
            true,
            """{ "InternalName": "NoId", "IsEnabled": true }""",
            """{ "InternalName": "EmptyId", "WorkingPluginId": "", "IsEnabled": true }""",
            """{ "InternalName": "ZeroId", "WorkingPluginId": "00000000-0000-0000-0000-000000000000", "IsEnabled": true }""",
            """{ "InternalName": "BadId", "WorkingPluginId": "not-a-guid", "IsEnabled": true }""",
            """{ "InternalName": "NumberId", "WorkingPluginId": 42, "IsEnabled": true }""",
            $$"""{ "WorkingPluginId": "{{SplatoonId}}", "IsEnabled": true }""",
            $$"""{ "InternalName": "", "WorkingPluginId": "{{AutoRetainerId}}", "IsEnabled": true }""",
            Plugin("Artisan", ArtisanId)));

        Assert.Equal([new PluginReference("Artisan", ArtisanId)], Read(json)!.Plugins);
    }

    [Fact]
    public void Drops_IDs_that_belong_to_dev_plugins()
    {
        var devId = Guid.Parse("e5d4c3b2-a190-4f8e-9d7c-6b5a49382716");
        var otherDevId = Guid.Parse("f6e5d4c3-b2a1-4098-8e7d-6c5b4a392817");
        var json = $$"""
            {
              "DevPluginSettings": {
                "$type": "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Dalamud.Configuration.Internal.DevPluginSettings, Dalamud]], System.Private.CoreLib",
                "C:\\Users\\tester\\source\\MyPlugin\\bin\\MyPlugin.dll": { "$type": "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud", "StartOnBoot": true, "WorkingPluginId": "{{devId}}" },
                "C:\\Users\\tester\\source\\Artisan\\bin\\Artisan.dll": { "StartOnBoot": true, "WorkingPluginId": "{{otherDevId}}" }
              },
              "SavedProfiles": {{List(true, Collection(true, Plugin("Artisan", ArtisanId)))}},
              "DefaultProfile": {{Collection(true, Plugin("MyPlugin", devId), Plugin("Artisan", otherDevId), Plugin("Splatoon", SplatoonId))}}
            }
            """;

        var settings = Read(json);

        // A dev build can share a released plugin's name; the released plugin's own ID is still used.
        Assert.NotNull(settings);
        PluginReference[] expected = [new("Splatoon", SplatoonId), new("Artisan", ArtisanId)];
        Assert.Equal(expected, settings.Plugins);
        Assert.DoesNotContain("source", Describe(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Drops_an_ID_that_two_plugins_share()
    {
        var shared = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c05");
        var json = Config(
            savedProfiles: List(true, Collection(true, Plugin("AutoRetainer", shared))),
            defaultProfile: Collection(true, Plugin("Artisan", shared), Plugin("Splatoon", SplatoonId)));

        Assert.Equal([new PluginReference("Splatoon", SplatoonId)], Read(json)!.Plugins);
    }

    [Fact]
    public void A_collection_s_last_entry_for_a_plugin_wins()
    {
        // After goatcorp/Dalamud#1992 a collection keeps the dead ID, and the live one is added after it.
        var dead = Guid.Parse("11111111-2222-4333-8444-555555555555");
        var live = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");
        var json = Config(savedProfiles: List(
            true,
            Collection(true, Plugin("Splatoon", dead)),
            Collection(true, Plugin("Splatoon", dead), Plugin("Artisan", ArtisanId), Plugin("Splatoon", live)),
            Collection(true, Plugin("AutoRetainer", AutoRetainerId))));

        PluginReference[] expected = [new("Splatoon", live), new("Artisan", ArtisanId), new("AutoRetainer", AutoRetainerId)];
        Assert.Equal(expected, Read(json)!.Plugins);
    }

    [Fact]
    public void The_default_collection_s_ID_wins()
    {
        var other = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
        var json = Config(
            savedProfiles: List(true, Collection(true, Plugin("Artisan", other))),
            defaultProfile: Collection(true, Plugin("Artisan", ArtisanId)));

        Assert.Equal([new PluginReference("Artisan", ArtisanId)], Read(json)!.Plugins);
    }

    [Fact]
    public void Lists_each_enabled_repository_once_exactly_as_written()
    {
        var json = """
            {
              "ThirdRepoList": {
                "$type": "System.Collections.Generic.List`1[[Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud]], System.Private.CoreLib",
                "$values": [
                  { "$type": "Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud", "Url": "https://one.example.com/repo.json", "IsEnabled": true },
                  { "Url": "https://off.example.com/repo.json", "IsEnabled": false },
                  { "Url": "", "IsEnabled": true },
                  { "Url": null, "IsEnabled": true },
                  { "IsEnabled": true },
                  { "Url": "https://one.example.com/repo.json", "IsEnabled": true },
                  { "Name": "Older files name it", "Url": "HTTPS://One.Example.com/repo.json/ ", "IsEnabled": true },
                  { "Url": "https://off.example.com/repo.json", "IsEnabled": true }
                ]
              }
            }
            """;

        Assert.Equal(
            ["https://one.example.com/repo.json", "HTTPS://One.Example.com/repo.json/ ", "https://off.example.com/repo.json"],
            Read(json)!.CustomRepositories);
    }

    [Fact]
    public void Reads_the_testing_choices()
    {
        var json = """
            {
              "DoPluginTest": true,
              "PluginTestingOptIns": {
                "$type": "System.Collections.Generic.List`1[[Dalamud.Configuration.Internal.PluginTestingOptIn, Dalamud]], System.Private.CoreLib",
                "$values": [
                  { "$type": "Dalamud.Configuration.Internal.PluginTestingOptIn, Dalamud", "InternalName": "Splatoon", "Branch": "testing-live" },
                  { "InternalName": "artisan", "Branch": "testing-live" },
                  { "Branch": "testing-live" }
                ]
              }
            }
            """;

        var settings = Read(json);

        Assert.NotNull(settings);
        Assert.True(settings.DoPluginTest);
        Assert.Equal(["Splatoon", "artisan"], settings.TestingOptIns.Order(StringComparer.Ordinal));
        Assert.False(settings.TestingOptIns.Contains("Artisan"), "Dalamud matches opt-ins by exact name.");
    }

    [Fact]
    public void Reads_comments_trailing_commas_and_names_in_any_case()
    {
        var json = $$"""
            {
              // Newtonsoft, which Dalamud reads its settings with, allows all of this.
              "defaultprofile": { "plugins": [ { "internalName": "Artisan", "workingpluginid": "{{ArtisanId}}", }, ], },
              /* a block comment */
              "THIRDREPOLIST": [ { "url": "https://one.example.com/repo.json", "isEnabled": true, }, ],
              "doPluginTest": true,
              "plugintestingoptins": [ { "internalname": "Artisan" } ],
            }
            """;

        var settings = Read(json);

        Assert.NotNull(settings);
        Assert.Equal([new PluginReference("Artisan", ArtisanId)], settings.Plugins);
        Assert.Equal(["https://one.example.com/repo.json"], settings.CustomRepositories);
        Assert.True(settings.DoPluginTest);
        Assert.Equal(["Artisan"], settings.TestingOptIns);
    }

    [Fact]
    public void Treats_sections_of_another_shape_as_missing()
    {
        var json = """
            {
              "DefaultProfile": "text",
              "SavedProfiles": [ 5, null, { "Plugins": { "$values": 7 } } ],
              "ThirdRepoList": { "Url": "https://one.example.com/repo.json" },
              "DoPluginTest": "true",
              "PluginTestingOptIns": [ "Artisan" ],
              "DevPluginSettings": [ { "WorkingPluginId": "0f3c6a52-5d0e-4c3b-9a35-1d2e7b8c9a01" } ]
            }
            """;

        var settings = Read(json);

        Assert.NotNull(settings);
        Assert.Empty(settings.Plugins);
        Assert.Empty(settings.CustomRepositories);
        Assert.False(settings.DoPluginTest);
        Assert.Empty(settings.TestingOptIns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"DefaultProfile\": ")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Returns_null_for_a_file_that_isn_t_a_config(string json) => Assert.Null(Read(json));

    [Fact]
    public void Reads_a_file_and_returns_null_when_it_is_missing()
    {
        using var env = new TestEnvironment();
        var path = Path.Combine(env.Root, "dalamudConfig.json");

        Assert.Null(DalamudConfigReader.ReadPluginSettings(path));

        File.WriteAllText(path, Config(defaultProfile: Collection(true, Plugin("Artisan", ArtisanId))));
        Assert.Equal([new PluginReference("Artisan", ArtisanId)], DalamudConfigReader.ReadPluginSettings(path)!.Plugins);
    }
}
