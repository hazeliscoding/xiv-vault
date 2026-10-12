using System.Text;
using XivVault.Core.Plugins;

namespace XivVault.Tests.Core.Plugins;

/// <summary>Repositories are read as Dalamud reads them with Newtonsoft's defaults, loose values included.</summary>
public class PluginRepositoryParserTests
{
    private static IReadOnlyList<RepositoryPlugin>? Parse(string json) =>
        PluginRepositoryParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static RepositoryPlugin ParseOne(string fields) =>
        Assert.Single(Parse($$"""[ { "InternalName": "Sample", "Name": "Sample", "AssemblyVersion": "1.0.0.0"{{fields}} } ]""")!);

    [Fact]
    public void An_official_entry_keeps_what_installing_needs()
    {
        // Shaped like an entry in kamori's list, which marks most entries hidden.
        var plugins = Parse("""
            [
              {
                "Author": "Someone",
                "Name": "Simple Tweaks Plugin",
                "Punchline": "This was supposed to be simple...",
                "Description": "A collection of tweaks.",
                "Changelog": "Fixes",
                "TestingChangelog": "fix typo",
                "Tags": null,
                "CategoryTags": null,
                "IsHide": true,
                "InternalName": "SimpleTweaksPlugin",
                "AssemblyVersion": "1.15.0.7",
                "TestingAssemblyVersion": "1.14.1.3",
                "IsTestingExclusive": false,
                "RepoUrl": "https://github.com/example/SimpleTweaksPlugin",
                "ApplicableVersion": "any",
                "DalamudApiLevel": 15,
                "TestingDalamudApiLevel": 15,
                "DownloadCount": 8789726,
                "LastUpdate": 1781617143,
                "DownloadLinkInstall": "https://kamori.goats.dev/Plugin/Download/SimpleTweaksPlugin?isUpdate=False&isTesting=False&branch=api9&isDip17=True",
                "DownloadLinkUpdate": "https://raw.githubusercontent.com/goatcorp/DalamudPlugins/api9/plugins/SimpleTweaksPlugin/latest.zip",
                "DownloadLinkTesting": "https://kamori.goats.dev/Plugin/Download/SimpleTweaksPlugin?isUpdate=False&isTesting=True&branch=api9&isDip17=True",
                "LoadPriority": 0,
                "ImageUrls": null,
                "IconUrl": null,
                "AcceptsFeedback": true,
                "FeedbackMessage": null,
                "FeedbackWebhook": null,
                "_isDip17Plugin": true,
                "_Dip17Channel": "stable"
              }
            ]
            """);

        Assert.Equal(
            new RepositoryPlugin(
                "SimpleTweaksPlugin",
                new Version(1, 15, 0, 7),
                new Version(1, 14, 1, 3),
                DalamudApiLevel: 15,
                TestingDalamudApiLevel: 15,
                IsTestingExclusive: false,
                IsHide: true,
                MinimumDalamudVersion: null,
                AnyGameVersion: true,
                "https://kamori.goats.dev/Plugin/Download/SimpleTweaksPlugin?isUpdate=False&isTesting=False&branch=api9&isDip17=True",
                "https://kamori.goats.dev/Plugin/Download/SimpleTweaksPlugin?isUpdate=False&isTesting=True&branch=api9&isDip17=True",
                Dip17Channel: "stable"),
            Assert.Single(plugins!));
    }

    [Fact]
    public void A_custom_entry_with_booleans_written_as_strings_is_read()
    {
        // Shaped like Penumbra's repository, which sends "False" as a string.
        var plugins = Parse("""
            [
              {
                "Author": "Someone",
                "Name": "Penumbra",
                "Punchline": "Runtime mod loader and manager.",
                "Description": "Runtime mod loader and manager.",
                "InternalName": "Penumbra",
                "AssemblyVersion": "1.7.2.1",
                "TestingAssemblyVersion": "1.7.2.1",
                "RepoUrl": "https://github.com/example/Penumbra",
                "ApplicableVersion": "any",
                "DalamudApiLevel": 15,
                "TestingDalamudApiLevel": 15,
                "IsHide": "False",
                "IsTestingExclusive": "False",
                "DownloadCount": 0,
                "LastUpdate": 0,
                "LoadPriority": 69420,
                "LoadRequiredState": 2,
                "LoadSync": true,
                "DownloadLinkInstall": "https://github.com/example/Penumbra/releases/download/1.7.2.1/Penumbra.zip",
                "DownloadLinkTesting": "https://github.com/example/Penumbra/releases/download/1.7.2.1/Penumbra.zip",
                "DownloadLinkUpdate": "https://github.com/example/Penumbra/releases/download/1.7.2.1/Penumbra.zip",
                "IconUrl": "https://raw.githubusercontent.com/example/Penumbra/master/images/icon.png"
              }
            ]
            """);

        Assert.Equal(
            new RepositoryPlugin(
                "Penumbra",
                new Version(1, 7, 2, 1),
                new Version(1, 7, 2, 1),
                DalamudApiLevel: 15,
                TestingDalamudApiLevel: 15,
                IsTestingExclusive: false,
                IsHide: false,
                MinimumDalamudVersion: null,
                AnyGameVersion: true,
                "https://github.com/example/Penumbra/releases/download/1.7.2.1/Penumbra.zip",
                "https://github.com/example/Penumbra/releases/download/1.7.2.1/Penumbra.zip",
                Dip17Channel: null),
            Assert.Single(plugins!));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"True\"", true)]
    [InlineData("\"FALSE\"", false)]
    [InlineData("\" true \"", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    public void Booleans_may_be_strings_in_any_case_or_numbers(string json, bool expected)
    {
        var plugin = ParseOne($$""", "IsHide": {{json}}, "IsTestingExclusive": {{json}}""");

        Assert.Equal(expected, plugin.IsHide);
        Assert.Equal(expected, plugin.IsTestingExclusive);
    }

    [Fact]
    public void Numbers_versions_and_text_convert_as_Newtonsoft_converts_them()
    {
        var plugin = ParseOne("""
            , "DalamudApiLevel": "15",
            "TestingDalamudApiLevel": "",
            "TestingAssemblyVersion": "",
            "MinimumDalamudVersion": "15.0.3.6",
            "DownloadLinkInstall": null,
            "_Dip17Channel": 17
            """);

        Assert.Equal(15, plugin.DalamudApiLevel);
        Assert.Null(plugin.TestingDalamudApiLevel);
        Assert.Null(plugin.TestingAssemblyVersion);
        Assert.Equal(new Version(15, 0, 3, 6), plugin.MinimumDalamudVersion);
        Assert.Null(plugin.DownloadLinkInstall);
        Assert.Equal("17", plugin.Dip17Channel);
    }

    [Fact]
    public void Fields_left_out_take_Dalamud_defaults()
    {
        var plugin = ParseOne("");

        Assert.Equal(
            new RepositoryPlugin("Sample", new Version(1, 0, 0, 0), null, null, null, false, false, null, true, null, null, null),
            plugin);
    }

    [Fact]
    public void Keys_match_without_case_and_a_repeated_key_keeps_its_last_value()
    {
        var plugins = Parse("""
            [
              {
                "internalname": "MixedCase",
                "NAME": "Mixed case",
                "assemblyVersion": "2.0.0.0",
                "dalamudapilevel": 14,
                "IsHide": false,
                "ishide": true,
                "_dip17channel": "testing-live",
                "downloadlinkinstall": "https://example.com/MixedCase.zip"
              }
            ]
            """);

        var plugin = Assert.Single(plugins!);
        Assert.Equal("MixedCase", plugin.InternalName);
        Assert.Equal(new Version(2, 0, 0, 0), plugin.AssemblyVersion);
        Assert.Equal(14, plugin.DalamudApiLevel);
        Assert.True(plugin.IsHide);
        Assert.Equal("testing-live", plugin.Dip17Channel);
        Assert.Equal("https://example.com/MixedCase.zip", plugin.DownloadLinkInstall);
    }

    [Theory]
    [InlineData(""" "IsHide": "maybe" """)]
    [InlineData(""" "IsHide": "" """)]
    [InlineData(""" "IsHide": null """)]
    [InlineData(""" "IsHide": "maybe", "isHide": false """)]
    [InlineData(""" "IsTestingExclusive": {} """)]
    [InlineData(""" "DalamudApiLevel": "fifteen" """)]
    [InlineData(""" "DalamudApiLevel": 15.5 """)]
    [InlineData(""" "DalamudApiLevel": 2147483648 """)]
    [InlineData(""" "DalamudApiLevel": null """)]
    [InlineData(""" "TestingDalamudApiLevel": true """)]
    [InlineData(""" "AssemblyVersion": "1" """)]
    [InlineData(""" "AssemblyVersion": 1.5 """)]
    [InlineData(""" "TestingAssemblyVersion": "latest" """)]
    [InlineData(""" "MinimumDalamudVersion": [] """)]
    [InlineData(""" "ApplicableVersion": "soon" """)]
    [InlineData(""" "ApplicableVersion": "" """)]
    [InlineData(""" "ApplicableVersion": "2024.1.1.1.1.1" """)]
    [InlineData(""" "ApplicableVersion": "2024.-1" """)]
    [InlineData(""" "ApplicableVersion": 2024 """)]
    [InlineData(""" "InternalName": {} """)]
    [InlineData(""" "Name": [] """)]
    [InlineData(""" "DownloadLinkInstall": {} """)]
    public void A_value_that_does_not_convert_fails_the_whole_repository(string field)
    {
        // The bad entry would also be dropped for its blank name, but Dalamud fails on the value first.
        var plugins = Parse($$"""
            [
              { "InternalName": "Good", "Name": "Good", "AssemblyVersion": "1.0.0.0" },
              { "InternalName": "Bad", "Name": "", "AssemblyVersion": "1.0.0.0", {{field}} }
            ]
            """);

        Assert.Null(plugins);
    }

    [Fact]
    public void Entries_Dalamud_drops_are_left_out_one_by_one()
    {
        var plugins = Parse("""
            [
              { "InternalName": "First", "Name": "First", "AssemblyVersion": "1.0.0.0" },
              { "InternalName": " ", "Name": "Blank internal name", "AssemblyVersion": "1.0.0.0" },
              { "Name": "No internal name", "AssemblyVersion": "1.0.0.0" },
              { "InternalName": "NoName", "AssemblyVersion": "1.0.0.0" },
              { "InternalName": "BlankName", "Name": "\t", "AssemblyVersion": "1.0.0.0" },
              { "InternalName": "NoVersion", "Name": "No version" },
              { "InternalName": "NullVersion", "Name": "Null version", "AssemblyVersion": null },
              { "InternalName": "EmptyVersion", "Name": "Empty version", "AssemblyVersion": "" },
              { "InternalName": "Last", "Name": "Last", "AssemblyVersion": "2.0.0.0" }
            ]
            """);

        Assert.Equal(["First", "Last"], plugins!.Select(plugin => plugin.InternalName));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("\"any\"", true)]
    [InlineData("\"ANY\"", true)]
    [InlineData("null", false)]
    [InlineData("\"2024.07.10.0001.0000\"", false)]
    public void ApplicableVersion_says_whether_the_plugin_works_with_any_game_version(string? json, bool anyGameVersion)
    {
        var plugin = ParseOne(json is null ? "" : $$""", "ApplicableVersion": {{json}}""");

        Assert.Equal(anyGameVersion, plugin.AnyGameVersion);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var plugins = Parse("""
            // A repository written by hand
            [
              {
                /* the stable build */
                "InternalName": "Commented",
                "Name": "Commented",
                "AssemblyVersion": "1.0.0.0", // a trailing comment
              },
            ]
            """);

        Assert.Equal("Commented", Assert.Single(plugins!).InternalName);
    }

    [Fact]
    public void An_empty_list_is_a_repository_with_no_plugins()
    {
        Assert.Empty(Parse("[]")!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "Plugins": [] }""")]
    [InlineData("\"plugins\"")]
    [InlineData("42")]
    [InlineData("not json")]
    [InlineData("""[ { "InternalName": "Cut off" """)]
    [InlineData("[ null ]")]
    [InlineData("[ 1 ]")]
    [InlineData("[ [] ]")]
    public void Anything_but_a_list_of_objects_fails_the_whole_repository(string json)
    {
        Assert.Null(Parse(json));
    }
}
