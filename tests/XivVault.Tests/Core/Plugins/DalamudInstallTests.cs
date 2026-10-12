using System.Security.Cryptography;
using System.Text;
using XivVault.Core.Plugins;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core.Plugins;

/// <summary>Which Dalamud XIVLauncher will run, and the plugin versions it refuses, read offline.</summary>
public class DalamudInstallTests : IDisposable
{
    private static readonly DateTime Earlier = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Latest = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestEnvironment _env = new();

    private string Root => _env.XivLauncherPath;

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Reads_the_version_of_the_Dalamud_prepared_last()
    {
        // Switching back from a beta track can make an older version the one prepared last.
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6), versionJsonWrittenUtc: Earlier);
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 5), versionJsonWrittenUtc: Later);

        Assert.Equal(new Version(15, 0, 3, 5), DalamudInstall.FindVersion(Root));
    }

    [Fact]
    public void Reads_the_DLL_instead_of_the_folder_name()
    {
        // Beta tracks name the folder, and version.json, with a git-describe string.
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6), folder: "15.0.3.6-32-g9ac5d052c");

        Assert.Equal(new Version(15, 0, 3, 6), DalamudInstall.FindVersion(Root));
    }

    [Fact]
    public void Skips_the_dev_folder()
    {
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6), versionJsonWrittenUtc: Earlier);
        FakeDalamud.InstallDalamud(Root, new Version(16, 0, 0, 0), folder: "dev", versionJsonWrittenUtc: Later);

        Assert.Equal(new Version(15, 0, 3, 6), DalamudInstall.FindVersion(Root));
    }

    [Fact]
    public void Skips_folders_missing_a_file()
    {
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6), versionJsonWrittenUtc: Earlier);
        var noDll = FakeDalamud.InstallDalamud(Root, new Version(15, 0, 4, 0), versionJsonWrittenUtc: Later);
        File.Delete(Path.Combine(noDll, "Dalamud.dll"));
        var noVersionJson = FakeDalamud.InstallDalamud(Root, new Version(15, 0, 5, 0), versionJsonWrittenUtc: Latest);
        File.Delete(Path.Combine(noVersionJson, "version.json"));

        Assert.Equal(new Version(15, 0, 3, 6), DalamudInstall.FindVersion(Root));
    }

    [Fact]
    public void A_DLL_that_is_not_an_assembly_gives_no_version()
    {
        FakeDalamud.InstallDalamud(Root, new Version(15, 0, 3, 6), versionJsonWrittenUtc: Earlier);
        var junk = FakeDalamud.InstallDalamud(Root, new Version(15, 0, 4, 0), versionJsonWrittenUtc: Later);
        File.WriteAllText(Path.Combine(junk, "Dalamud.dll"), "binary");

        Assert.Null(DalamudInstall.FindVersion(Root));
    }

    [Fact]
    public void No_Dalamud_gives_no_version()
    {
        Assert.Null(DalamudInstall.FindVersion(Root));

        // XIVLauncher closed before it finished: a DLL loose in Hooks, an empty version folder.
        var launcher = FakeXivLauncher.Create(Root);
        Directory.CreateDirectory(Path.Combine(launcher.Root, "addon", "Hooks", "15.0.3.6"));
        Assert.Null(DalamudInstall.FindVersion(launcher.Root));
    }

    [Fact]
    public void Bans_what_Dalamud_s_own_list_bans()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Artisan")));
        FakeDalamud.WriteBanList(Root, $$"""
            [
              { "Name": "Splatoon", "AssemblyVersion": "2.0.0.0", "Reason": "Crashes the game" },
              { "Name": "AutoRetainer", "AssemblyVersion": null, "Reason": "Every version" },
              { "Name": "Questionable", "Reason": "No version listed" },
              { "Name": "{{hash}}", "AssemblyVersion": "1.0.0.0", "Reason": "Listed by hash" },
              { "Name": "{{hash.ToLowerInvariant()}}", "Reason": "Dalamud never matches a lowercase hash" }
            ]
            """);

        var bans = DalamudInstall.LoadBanList(Root);

        Assert.True(bans.IsBanned("Splatoon", new Version(2, 0, 0, 0)));
        Assert.True(bans.IsBanned("Splatoon", new Version(1, 5)));
        Assert.False(bans.IsBanned("Splatoon", new Version(2, 0, 0, 1)));
        Assert.False(bans.IsBanned("splatoon", new Version(1, 0)));
        Assert.True(bans.IsBanned("AutoRetainer", new Version(99, 0)));
        Assert.True(bans.IsBanned("Questionable", new Version(99, 0)));
        Assert.True(bans.IsBanned("Artisan", new Version(1, 0, 0, 0)));
        Assert.False(bans.IsBanned("Artisan", new Version(1, 0, 0, 1)));
        Assert.False(bans.IsBanned("Pandora's Box", new Version(1, 0)));
    }

    [Fact]
    public void Reads_keys_without_case_as_Dalamud_does()
    {
        FakeDalamud.WriteBanList(Root, """[ { "name": "Splatoon", "assemblyVersion": "2.0.0.0", "REASON": "x" } ]""");

        var bans = DalamudInstall.LoadBanList(Root);

        Assert.True(bans.IsBanned("Splatoon", new Version(2, 0, 0, 0)));
        Assert.False(bans.IsBanned("Splatoon", new Version(3, 0)));
    }

    [Theory]
    [InlineData("\"not a version\"")]
    [InlineData("\"\"")]
    [InlineData("15")]
    [InlineData("{}")]
    public void An_unreadable_version_bans_every_version(string version)
    {
        FakeDalamud.WriteBanList(Root, $$"""[ { "Name": "Splatoon", "AssemblyVersion": {{version}}, "Reason": "x" } ]""");

        Assert.True(DalamudInstall.LoadBanList(Root).IsBanned("Splatoon", new Version(99, 0)));
    }

    [Fact]
    public void Without_a_ban_list_nothing_is_banned()
    {
        Assert.Same(DalamudBanList.Empty, DalamudInstall.LoadBanList(Root));

        var assets = Path.Combine(Root, "dalamudAssets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "asset.ver"), "444");
        Assert.Same(DalamudBanList.Empty, DalamudInstall.LoadBanList(Root));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void A_list_that_can_t_be_read_bans_nothing(string json)
    {
        FakeDalamud.WriteBanList(Root, json);

        Assert.Same(DalamudBanList.Empty, DalamudInstall.LoadBanList(Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("444/../../evil")]
    [InlineData("444\\..\\..\\evil")]
    public void An_unsafe_assets_version_is_refused(string assetVersion)
    {
        const string Bans = """[ { "Name": "Splatoon", "AssemblyVersion": null, "Reason": "x" } ]""";
        FakeDalamud.WriteBanList(Root, Bans);
        var evil = Path.Combine(Root, "evil", "UIRes");
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "bannedplugin.json"), Bans);
        File.WriteAllText(Path.Combine(Root, "dalamudAssets", "asset.ver"), assetVersion);

        Assert.Same(DalamudBanList.Empty, DalamudInstall.LoadBanList(Root));
    }
}
