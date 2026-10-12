using System.Security.Cryptography;
using System.Text;
using XivVault.Core.Plugins;

namespace XivVault.Tests.Core.Plugins;

/// <summary>The planner decides which plugins to install as Dalamud's installer would, and why the rest aren't.</summary>
public class InstallPlannerTests
{
    private const string CustomUrl = "https://Raw.Example.com/Someone/Plugins/main/repo.json";
    private const string OtherUrl = "https://plugins.example.org/pluginmaster.json";

    private static readonly Version Dalamud = new(15, 0, 3, 6);
    private static readonly PluginReference Foo = new("Foo", Guid.Parse("0b6ee4f5-1c2a-4d2e-9a43-6f0f2d4f7a11"));

    private static RepositoryPlugin Entry(string name) => new(
        name,
        new Version(1, 2, 0, 0),
        TestingAssemblyVersion: null,
        DalamudApiLevel: 15,
        TestingDalamudApiLevel: null,
        IsTestingExclusive: false,
        IsHide: false,
        MinimumDalamudVersion: null,
        AnyGameVersion: true,
        DownloadLinkInstall: $"https://kamori.example.com/Plugin/Download/{name}?isTesting=False",
        DownloadLinkTesting: $"https://kamori.example.com/Plugin/Download/{name}?isTesting=True",
        Dip17Channel: "stable");

    /// <summary>Foo with a newer testing build for this API level.</summary>
    private static RepositoryPlugin WithTesting(RepositoryPlugin entry) =>
        entry with { TestingAssemblyVersion = new Version(1, 3, 0, 0), TestingDalamudApiLevel = 15 };

    private static InstallPlan Plan(
        IReadOnlyList<RepositoryPlugin>? official = null,
        IReadOnlyList<CustomRepositoryList>? custom = null,
        InstalledPlugins? installed = null,
        DalamudBanList? bans = null,
        bool doPluginTest = false,
        string[]? optIns = null,
        IReadOnlyList<PluginReference>? plugins = null) =>
        InstallPlanner.Plan(
            plugins ?? [Foo],
            installed ?? InstalledPlugins.None,
            Dalamud,
            official ?? [],
            custom ?? [],
            bans ?? DalamudBanList.Empty,
            doPluginTest,
            (optIns ?? []).ToHashSet(StringComparer.Ordinal));

    private static PlannedInstall Planned(InstallPlan plan)
    {
        Assert.Empty(plan.Skipped);
        return Assert.Single(plan.Installs);
    }

    private static PluginInstallOutcome Skipped(InstallPlan plan)
    {
        Assert.Empty(plan.Installs);
        return Assert.Single(plan.Skipped).Outcome;
    }

    private static InstalledPlugins WithFolder(string name, PluginFolderState state) =>
        InstalledPlugins.None with { Folders = new Dictionary<string, PluginFolderState>(StringComparer.OrdinalIgnoreCase) { [name] = state } };

    [Fact]
    public void An_official_plugin_installs_its_stable_build_from_the_official_repository()
    {
        var entry = Entry("Foo");

        var install = Planned(Plan(official: [entry]));

        Assert.Equal(new PlannedInstall(Foo, entry, RepositoryUrl: null, UseTesting: false, new Version(1, 2, 0, 0), entry.DownloadLinkInstall!), install);
    }

    [Fact]
    public void A_custom_plugin_installs_from_its_repository_with_the_URL_exactly_as_given()
    {
        var first = Entry("Foo") with { DownloadLinkInstall = "https://example.com/first.zip" };
        var second = Entry("Foo") with { DownloadLinkInstall = "https://example.com/second.zip" };

        var install = Planned(Plan(official: [Entry("Other")], custom: [new CustomRepositoryList(CustomUrl, [Entry("Bar"), first, second])]));

        Assert.Equal(CustomUrl, install.RepositoryUrl);
        Assert.Same(first, install.Entry);
        Assert.Equal("https://example.com/first.zip", install.DownloadLink);
    }

    [Theory]
    [InlineData(PluginFolderState.Installed, PluginInstallOutcome.AlreadyInstalled)]
    [InlineData(PluginFolderState.InTheWay, PluginInstallOutcome.InTheWay)]
    [InlineData(PluginFolderState.Link, PluginInstallOutcome.LinkedFolder)]
    public void A_plugin_with_a_folder_is_left_alone(PluginFolderState state, PluginInstallOutcome expected)
    {
        // Windows matches folder names without case, so the scanner's list does too.
        var plan = Plan(official: [Entry("Foo")], installed: WithFolder("foo", state));

        Assert.Equal(expected, Skipped(plan));
    }

    [Fact]
    public void A_linked_plugins_folder_installs_nothing()
    {
        var plan = Plan(
            official: [Entry("Foo")],
            installed: InstalledPlugins.None with { IsLink = true },
            plugins: [Foo, new PluginReference("Missing", Guid.NewGuid())]);

        Assert.Empty(plan.Installs);
        Assert.All(plan.Skipped, skipped => Assert.Equal(PluginInstallOutcome.LinkedFolder, skipped.Outcome));
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public void An_ID_another_installed_plugin_uses_is_never_given_out_again()
    {
        var plan = Plan(official: [Entry("Foo")], installed: InstalledPlugins.None with { Ids = new HashSet<Guid> { Foo.WorkingPluginId } });

        Assert.Equal(PluginInstallOutcome.IdInUse, Skipped(plan));
    }

    [Fact]
    public void A_plugin_no_repository_offers_is_not_offered()
    {
        Assert.Equal(PluginInstallOutcome.NotOffered, Skipped(Plan(official: [Entry("Bar")])));
        Assert.Equal(PluginInstallOutcome.NotOffered, Skipped(Plan(official: [Entry("Bar")], custom: [new CustomRepositoryList(CustomUrl, [])])));
    }

    [Fact]
    public void A_repository_that_could_not_be_read_may_have_held_the_plugin()
    {
        var unavailable = new CustomRepositoryList(OtherUrl, Plugins: null);

        Assert.Equal(
            PluginInstallOutcome.RepositoryUnavailable,
            Skipped(Plan(official: [Entry("Bar")], custom: [new CustomRepositoryList(CustomUrl, [Entry("Baz")]), unavailable])));

        // A repository that does offer it is still the source.
        Assert.Equal(CustomUrl, Planned(Plan(custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo")]), unavailable])).RepositoryUrl);
    }

    [Fact]
    public void A_name_two_chosen_repositories_offer_is_skipped()
    {
        var plan = Plan(custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo")]), new CustomRepositoryList(OtherUrl, [Entry("Foo")])]);

        Assert.Equal(PluginInstallOutcome.OfferedTwice, Skipped(plan));
    }

    [Fact]
    public void An_official_name_is_the_only_source_even_when_it_cannot_be_installed()
    {
        var plan = Plan(
            official: [Entry("Foo") with { DalamudApiLevel = 9 }],
            custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo")])]);

        Assert.Equal(PluginInstallOutcome.NotUpdated, Skipped(plan));
    }

    [Fact]
    public void An_official_name_hides_custom_entries_that_differ_only_in_case()
    {
        var plan = Plan(
            official: [Entry("FOO") with { DalamudApiLevel = 9 }],
            custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo")])]);

        Assert.Equal(PluginInstallOutcome.NotOffered, Skipped(plan));
    }

    [Fact]
    public void Hidden_official_entries_install_and_hidden_custom_entries_do_not()
    {
        Assert.Null(Planned(Plan(official: [Entry("Foo") with { IsHide = true }])).RepositoryUrl);

        Assert.Equal(
            PluginInstallOutcome.NotOffered,
            Skipped(Plan(custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo") with { IsHide = true }])])));

        // A hidden entry doesn't count as a second offer either.
        var plan = Plan(custom: [new CustomRepositoryList(CustomUrl, [Entry("Foo") with { IsHide = true }]), new CustomRepositoryList(OtherUrl, [Entry("Foo")])]);
        Assert.Equal(OtherUrl, Planned(plan).RepositoryUrl);
    }

    [Fact]
    public void A_testing_build_installs_where_the_settings_opted_in()
    {
        var entry = WithTesting(Entry("Foo"));

        var install = Planned(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"]));

        Assert.True(install.UseTesting);
        Assert.Equal(new Version(1, 3, 0, 0), install.Version);
        Assert.Equal(entry.DownloadLinkTesting, install.DownloadLink);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Without_testing_turned_on_and_an_opt_in_the_stable_build_installs(bool doPluginTest, bool optedIn)
    {
        var entry = WithTesting(Entry("Foo"));

        var install = Planned(Plan(official: [entry], doPluginTest: doPluginTest, optIns: optedIn ? ["Foo"] : []));

        Assert.False(install.UseTesting);
        Assert.Equal(new Version(1, 2, 0, 0), install.Version);
        Assert.Equal(entry.DownloadLinkInstall, install.DownloadLink);
    }

    [Fact]
    public void A_stale_testing_build_older_than_stable_is_not_used()
    {
        var entry = Entry("Foo") with { TestingAssemblyVersion = new Version(1, 1, 0, 0), TestingDalamudApiLevel = 15 };

        var install = Planned(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"]));

        Assert.False(install.UseTesting);
        Assert.Equal(new Version(1, 2, 0, 0), install.Version);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(null)]
    public void A_testing_build_for_another_API_level_is_not_used(int? testingApiLevel)
    {
        var entry = WithTesting(Entry("Foo")) with { TestingDalamudApiLevel = testingApiLevel };

        var install = Planned(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"]));

        Assert.False(install.UseTesting);
        Assert.Equal(new Version(1, 2, 0, 0), install.Version);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_testing_only_plugin_needs_testing_turned_on_and_an_opt_in(bool doPluginTest, bool optedIn)
    {
        var entry = WithTesting(Entry("Foo")) with { IsTestingExclusive = true };

        var plan = Plan(official: [entry], doPluginTest: doPluginTest, optIns: optedIn ? ["Foo"] : []);

        Assert.Equal(PluginInstallOutcome.TestingOnly, Skipped(plan));
    }

    [Fact]
    public void A_testing_only_plugin_installs_its_testing_build_when_opted_in()
    {
        // Whatever its version, and with its API level left out.
        var entry = Entry("Foo") with { IsTestingExclusive = true, TestingAssemblyVersion = new Version(1, 0, 0, 0) };

        var install = Planned(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"]));

        Assert.True(install.UseTesting);
        Assert.Equal(new Version(1, 0, 0, 0), install.Version);
        Assert.Equal(entry.DownloadLinkTesting, install.DownloadLink);
    }

    [Fact]
    public void A_testing_only_plugin_without_a_testing_version_is_not_offered()
    {
        var entry = Entry("Foo") with { IsTestingExclusive = true, TestingDalamudApiLevel = 15 };

        Assert.Equal(PluginInstallOutcome.NotOffered, Skipped(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"])));
    }

    [Theory]
    [InlineData(14, PluginInstallOutcome.NotUpdated)]
    [InlineData(9, PluginInstallOutcome.NotUpdated)]
    [InlineData(16, PluginInstallOutcome.NeedsNewerDalamud)]
    public void Only_a_plugin_for_this_API_level_installs(int apiLevel, PluginInstallOutcome expected)
    {
        Assert.Equal(expected, Skipped(Plan(official: [Entry("Foo") with { DalamudApiLevel = apiLevel }])));
    }

    [Fact]
    public void A_left_out_API_level_means_this_one()
    {
        Assert.Equal(new Version(1, 2, 0, 0), Planned(Plan(official: [Entry("Foo") with { DalamudApiLevel = null }])).Version);
    }

    [Fact]
    public void A_testing_build_is_held_to_its_own_API_level()
    {
        var entry = WithTesting(Entry("Foo")) with { IsTestingExclusive = true, TestingDalamudApiLevel = 14 };

        Assert.Equal(PluginInstallOutcome.NotUpdated, Skipped(Plan(official: [entry], doPluginTest: true, optIns: ["Foo"])));
    }

    [Fact]
    public void A_plugin_that_needs_a_newer_Dalamud_version_is_skipped()
    {
        Assert.Equal(
            PluginInstallOutcome.NeedsNewerDalamud,
            Skipped(Plan(official: [Entry("Foo") with { MinimumDalamudVersion = new Version(15, 0, 4, 0) }])));

        Planned(Plan(official: [Entry("Foo") with { MinimumDalamudVersion = new Version(15, 0, 3, 6) }]));
    }

    [Fact]
    public void A_plugin_made_for_one_game_version_is_skipped()
    {
        Assert.Equal(PluginInstallOutcome.NotForThisGameVersion, Skipped(Plan(official: [Entry("Foo") with { AnyGameVersion = false }])));
    }

    [Theory]
    [InlineData("Foo", null, true)]
    [InlineData("Foo", "1.2.0.0", true)]
    [InlineData("Foo", "1.3.0.0", true)]
    [InlineData("Foo", "1.1.9.9", false)]
    [InlineData("foo", null, false)]
    [InlineData("Bar", null, false)]
    public void The_ban_list_matches_the_name_exactly_up_to_its_version(string name, string? version, bool banned)
    {
        var bans = new DalamudBanList([(name, version is null ? null : Version.Parse(version))]);

        var plan = Plan(official: [Entry("Foo")], bans: bans);

        Assert.Equal(banned, plan.Skipped.Any(skipped => skipped.Outcome == PluginInstallOutcome.Banned));
        Assert.Equal(banned, plan.Installs.Count == 0);
    }

    [Fact]
    public void A_ban_may_name_the_uppercase_SHA_256_of_the_plugin_name()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Foo")));

        Assert.Equal(PluginInstallOutcome.Banned, Skipped(Plan(official: [Entry("Foo")], bans: new DalamudBanList([(hash, null)]))));
        Planned(Plan(official: [Entry("Foo")], bans: new DalamudBanList([(hash.ToLowerInvariant(), null)])));
    }

    [Fact]
    public void A_ban_is_checked_against_the_build_that_would_install()
    {
        var entry = WithTesting(Entry("Foo"));
        var bans = new DalamudBanList([("Foo", new Version(1, 2, 0, 0))]);

        Assert.Equal(PluginInstallOutcome.Banned, Skipped(Plan(official: [entry], bans: bans)));
        Assert.True(Planned(Plan(official: [entry], bans: bans, doPluginTest: true, optIns: ["Foo"])).UseTesting);
    }

    [Theory]
    [InlineData("http://kamori.example.com/Plugin/Download/Foo")]
    [InlineData("/Plugin/Download/Foo")]
    [InlineData("Plugin/Download/Foo")]
    [InlineData("file:///C:/Plugins/Foo.zip")]
    [InlineData("ftp://example.com/Foo.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_an_absolute_HTTPS_link_is_downloaded(string? link)
    {
        Assert.Equal(PluginInstallOutcome.PackageRefused, Skipped(Plan(official: [Entry("Foo") with { DownloadLinkInstall = link }])));

        var testing = WithTesting(Entry("Foo")) with { DownloadLinkTesting = link };
        Assert.Equal(PluginInstallOutcome.PackageRefused, Skipped(Plan(official: [testing], doPluginTest: true, optIns: ["Foo"])));
    }

    [Fact]
    public void Plugins_keep_their_order()
    {
        var plugins = new[] { "Delta", "Alpha", "Missing", "Charlie", "Old" }.Select(name => new PluginReference(name, Guid.NewGuid())).ToList();

        var plan = Plan(
            official: [Entry("Alpha"), Entry("Charlie"), Entry("Delta"), Entry("Old") with { DalamudApiLevel = 14 }],
            plugins: plugins);

        Assert.Equal(["Delta", "Alpha", "Charlie"], plan.Installs.Select(install => install.Plugin.InternalName));
        Assert.Equal(
            [new PluginOutcome("Missing", PluginInstallOutcome.NotOffered), new PluginOutcome("Old", PluginInstallOutcome.NotUpdated)],
            plan.Skipped);
        Assert.Equal(plugins.Where(plugin => plugin.InternalName is "Delta" or "Alpha" or "Charlie"), plan.Installs.Select(install => install.Plugin));
    }
}
