using System.Text;
using System.Text.Json;
using XivVault.Core.Plugins;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>
/// The expected bytes are what Newtonsoft.Json 13.0.4, the version Dalamud 15.0.3.6 ships, writes
/// for the same packaged manifest after the installer's edits.
/// </summary>
public class PluginManifestFileTests
{
    // Characters Newtonsoft writes raw, spelled out so the expected bytes stay readable here.
    private const string Del = "\u007F";
    private const string Nbsp = "\u00A0";
    private const string IdeographicSpace = "\u3000";
    private const string ByteOrderMark = "\uFEFF";
    private const string PrivateUse = "\uE000";
    private const string Emoji = "\U0001F600";
    private const string EAcute = "\u00E9";

    private static readonly Guid PluginId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static byte[] ForInstall(string packaged, Guid id, bool testing = false, string installedFromUrl = "OFFICIAL", string? dip17Channel = "stable", int apiLevel = 15)
    {
        using var document = JsonDocument.Parse(packaged, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return PluginManifestFile.ForInstall(document.RootElement, id, testing, installedFromUrl, dip17Channel, apiLevel);
    }

    private static byte[] Golden(string text) => Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));

    private static JsonElement Field(byte[] manifest, string name) => JsonDocument.Parse(manifest).RootElement.GetProperty(name);

    [Fact]
    public void A_typical_manifest_is_written_byte_for_byte_as_Dalamud_writes_it()
    {
        var expected = Golden("""
            {
              "Disabled": false,
              "Testing": false,
              "ScheduledForDeletion": false,
              "InstalledFromUrl": "OFFICIAL",
              "WorkingPluginId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
              "IsThirdParty": false,
              "Author": "Hazel",
              "Name": "Test Plugin",
              "Punchline": "A plugin for tests.",
              "Description": "Does one thing.\nThen another.",
              "Changelog": null,
              "Tags": [
                "Utility",
                "UI"
              ],
              "CategoryTags": null,
              "IsHide": false,
              "InternalName": "TestPlugin",
              "AssemblyVersion": "1.2.3.4",
              "RepoUrl": "https://github.com/example/TestPlugin",
              "ApplicableVersion": "any",
              "MinimumDalamudVersion": null,
              "DalamudApiLevel": 15,
              "DownloadCount": 0,
              "LastUpdate": 0,
              "DownloadLinkInstall": null,
              "DownloadLinkUpdate": null,
              "DownloadLinkTesting": null,
              "LoadRequiredState": 0,
              "LoadSync": false,
              "LoadPriority": 0,
              "CanUnloadAsync": false,
              "SupportsProfiles": true,
              "ImageUrls": null,
              "IconUrl": null,
              "AcceptsFeedback": true,
              "FeedbackMessage": null,
              "_Dip17Channel": "stable",
              "TestingAssemblyVersion": null,
              "TestingDalamudApiLevel": null,
              "IsTestingExclusive": false
            }
            """);

        var actual = ForInstall(FakePluginPackage.TypicalManifest().ToJsonString(), PluginId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_hostile_manifest_is_read_and_written_as_Newtonsoft_reads_and_writes_it()
    {
        const string packaged = """
            {
              // Comments and trailing commas are allowed, as Newtonsoft allows them.
              "author": "Q\" B\\ S/ \b\f\n\r\t \u0000\u0001\u001f \u007f \u0085 \u00a0 \u2028\u2029 \u3000 \ufeff \ue000 \ud83d\ude00 \u00e9 <>&'",
              "NAME": "Hostile",
              "internalname": "Hostile",
              "AssemblyVersion": "01.002",
              "MinimumDalamudVersion": "015.0.03",
              "TestingAssemblyVersion": "2.0",
              "DalamudApiLevel": "15",
              "TestingDalamudApiLevel": "",
              "LoadPriority": " -3 ",
              "DownloadCount": "42",
              "LastUpdate": 1700000000,
              "isHide": "True",
              "LoadSync": 1,
              "SupportsProfiles": "false",
              "AcceptsFeedback": 0,
              "Tags": ["a", 7, true, null],
              "CategoryTags": [],
              "ImageUrls": "",
              "IconUrl": 3.50,
              "Changelog": false,
              "Testing": "true",
              "Disabled": "FALSE",
              "InstalledFromUrl": "replaced",
              "WorkingPluginId": "{00000000-0000-0000-0000-000000000000}",
              "IsThirdParty": "False",
              "_dip17channel": "replaced",
              "Dip17Channel": "not a field",
              "Unknown": {"Nested": [1, {"Deep": null}]},
              "IsAvailableForTesting": [],
              "ApplicableVersion": "ANY",
              // A repeated key overwrites and a repeated list grows, as in Newtonsoft.
              "Punchline": "first",
              "punchline": "last",
              "tags": ["more"],
            }
            """;
        var expected = Golden($$"""
            {
              "Disabled": false,
              "Testing": true,
              "ScheduledForDeletion": false,
              "InstalledFromUrl": "https://example.com/repo.json",
              "WorkingPluginId": "3f2504e0-4f89-41d3-9a0c-0305e82c3302",
              "IsThirdParty": true,
              "Author": "Q\" B\\ S/ \b\f\n\r\t \u0000\u0001\u001f {{Del}} \u0085 {{Nbsp}} \u2028\u2029 {{IdeographicSpace}} {{ByteOrderMark}} {{PrivateUse}} {{Emoji}} {{EAcute}} <>&'",
              "Name": "Hostile",
              "Punchline": "last",
              "Description": null,
              "Changelog": "false",
              "Tags": [
                "a",
                "7",
                "true",
                null,
                "more"
              ],
              "CategoryTags": [],
              "IsHide": true,
              "InternalName": "Hostile",
              "AssemblyVersion": "1.2",
              "RepoUrl": null,
              "ApplicableVersion": "any",
              "MinimumDalamudVersion": "15.0.3",
              "DalamudApiLevel": 15,
              "DownloadCount": 42,
              "LastUpdate": 1700000000,
              "DownloadLinkInstall": null,
              "DownloadLinkUpdate": null,
              "DownloadLinkTesting": null,
              "LoadRequiredState": 0,
              "LoadSync": true,
              "LoadPriority": -3,
              "CanUnloadAsync": false,
              "SupportsProfiles": false,
              "ImageUrls": null,
              "IconUrl": "3.50",
              "AcceptsFeedback": false,
              "FeedbackMessage": null,
              "_Dip17Channel": "testing-live",
              "TestingAssemblyVersion": "2.0",
              "TestingDalamudApiLevel": null,
              "IsTestingExclusive": false
            }
            """);

        var actual = ForInstall(packaged, Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3302"), installedFromUrl: "https://example.com/repo.json", dip17Channel: "testing-live");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Missing_keys_get_Dalamuds_defaults_with_the_running_API_level()
    {
        var expected = Golden("""
            {
              "Disabled": false,
              "Testing": true,
              "ScheduledForDeletion": false,
              "InstalledFromUrl": "",
              "WorkingPluginId": "00000000-0000-0000-0000-000000000001",
              "IsThirdParty": false,
              "Author": null,
              "Name": null,
              "Punchline": null,
              "Description": null,
              "Changelog": null,
              "Tags": null,
              "CategoryTags": null,
              "IsHide": false,
              "InternalName": null,
              "AssemblyVersion": null,
              "RepoUrl": null,
              "ApplicableVersion": "any",
              "MinimumDalamudVersion": null,
              "DalamudApiLevel": 16,
              "DownloadCount": 0,
              "LastUpdate": 0,
              "DownloadLinkInstall": null,
              "DownloadLinkUpdate": null,
              "DownloadLinkTesting": null,
              "LoadRequiredState": 0,
              "LoadSync": false,
              "LoadPriority": 0,
              "CanUnloadAsync": false,
              "SupportsProfiles": true,
              "ImageUrls": null,
              "IconUrl": null,
              "AcceptsFeedback": true,
              "FeedbackMessage": null,
              "_Dip17Channel": null,
              "TestingAssemblyVersion": null,
              "TestingDalamudApiLevel": null,
              "IsTestingExclusive": false
            }
            """);

        var actual = ForInstall("{}", Guid.Parse("00000000-0000-0000-0000-000000000001"), testing: true, installedFromUrl: "", dip17Channel: null, apiLevel: 16);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("OFFICIAL", false)]
    [InlineData("https://example.com/repo.json", true)]
    [InlineData("", false)]
    public void Only_a_custom_repository_makes_a_plugin_third_party(string installedFromUrl, bool thirdParty)
    {
        var manifest = ForInstall("""{ "InstalledFromUrl": "OFFICIAL" }""", PluginId, installedFromUrl: installedFromUrl);

        Assert.Equal(installedFromUrl, Field(manifest, "InstalledFromUrl").GetString());
        Assert.Equal(thirdParty, Field(manifest, "IsThirdParty").GetBoolean());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void A_testing_install_is_marked_and_otherwise_the_packaged_value_stays(bool packaged, bool testing, bool expected)
    {
        var manifest = ForInstall($$"""{ "Testing": {{(packaged ? "true" : "false")}} }""", PluginId, testing: testing);

        Assert.Equal(expected, Field(manifest, "Testing").GetBoolean());
    }

    [Fact]
    public void The_backup_ID_replaces_any_packaged_ID_in_lowercase_D_format()
    {
        var manifest = ForInstall("""{ "WorkingPluginId": "00000000-0000-0000-0000-000000000000" }""", Guid.Parse("{ABCDEF01-2345-6789-ABCD-EF0123456789}"));

        Assert.Equal("abcdef01-2345-6789-abcd-ef0123456789", Field(manifest, "WorkingPluginId").GetString());
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{ "DalamudApiLevel": "fifteen" }""")]
    [InlineData("""{ "DalamudApiLevel": 15.0 }""")]
    [InlineData("""{ "DalamudApiLevel": "" }""")]
    [InlineData("""{ "LoadPriority": 2147483648 }""")]
    [InlineData("""{ "IsHide": "yes" }""")]
    [InlineData("""{ "IsHide": null }""")]
    [InlineData("""{ "IsThirdParty": {} }""")]
    [InlineData("""{ "DownloadCount": "" }""")]
    [InlineData("""{ "AssemblyVersion": "1" }""")]
    [InlineData("""{ "AssemblyVersion": 1.2 }""")]
    [InlineData("""{ "MinimumDalamudVersion": "1.2.3.4.5" }""")]
    [InlineData("""{ "WorkingPluginId": "not an ID" }""")]
    [InlineData("""{ "WorkingPluginId": null }""")]
    [InlineData("""{ "Author": {} }""")]
    [InlineData("""{ "Tags": "Utility" }""")]
    [InlineData("""{ "Tags": [["nested"]] }""")]
    [InlineData("""{ "ApplicableVersion": 7 }""")]
    public void A_value_Dalamud_cannot_convert_is_refused(string packaged)
    {
        Assert.Throws<InvalidDataException>(() => ForInstall(packaged, PluginId));
    }
}
