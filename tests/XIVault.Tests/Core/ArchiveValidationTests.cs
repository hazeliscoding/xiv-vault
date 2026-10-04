using XIVault.Core.Backup;
using XIVault.Tests.Support;

namespace XIVault.Tests.Core;

public class ArchiveValidationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly ArchiveValidator _validator = new();

    private string ArchivePath => Path.Combine(_env.Root, "test.zip");

    public void Dispose() => _env.Dispose();

    private ArchiveValidation Validate(ArchiveBuilder builder) =>
        _validator.Validate(builder.Save(ArchivePath), verifyContents: true, TestContext.Current.CancellationToken);

    [Fact]
    public void A_well_formed_archive_is_valid()
    {
        var result = Validate(new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .File("payload/pluginConfigs/Artisan.json", "{}"));

        Assert.True(result.IsValid, result.Summary);
        Assert.NotNull(result.ArchiveSha256);
    }

    [Theory]
    [InlineData("payload/../../evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("payload/pluginConfigs/../../../Windows/evil.dll")]
    [InlineData("payload/pluginConfigs/..\\..\\evil.dll")]
    public void Traversal_paths_are_rejected(string path)
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").File(path, "evil"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.UnsafePath);
    }

    [Theory]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("/etc/evil")]
    [InlineData("payload/pluginConfigs/C:evil.json")]
    [InlineData("payload/pluginConfigs/NUL.json")]
    [InlineData("payload/pluginConfigs/trailing.")]
    public void Absolute_and_device_paths_are_rejected(string path)
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").File(path, "evil"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.UnsafePath);
    }

    [Fact]
    public void Files_the_manifest_does_not_list_are_rejected()
    {
        var result = Validate(new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .UnlistedEntry("payload/pluginConfigs/Sneaky.json", "{}"));

        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.UnexpectedEntry);
    }

    [Fact]
    public void Files_outside_the_allowlist_are_rejected_even_when_listed()
    {
        var result = Validate(new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .File("payload/installedPlugins/Artisan/Artisan.dll", "binary"));

        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.NotAllowlisted);
    }

    [Fact]
    public void Checksum_mismatch_is_rejected()
    {
        var result = Validate(new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .FileWithWrongHash("payload/pluginConfigs/AutoRetainer.json", "{ \"x\": 1 }"));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ArchiveIssueCode.ChecksumMismatch, issue.Code);
        Assert.Equal("payload/pluginConfigs/AutoRetainer.json", issue.EntryPath);
    }

    [Fact]
    public void Malformed_manifest_is_rejected()
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").RawManifest("{ \"schemaVersion\": 1, \"files\": [ "));

        Assert.Equal(ArchiveIssueCode.MalformedManifest, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Manifest_missing_required_fields_is_rejected()
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").RawManifest("""{ "schemaVersion": 1, "files": null }"""));

        Assert.Equal(ArchiveIssueCode.MalformedManifest, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Legacy_manifest_without_a_schema_version_is_unsupported()
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").SchemaVersion(null));

        Assert.Equal(ArchiveIssueCode.UnsupportedSchema, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Manifest_from_a_newer_XIVault_is_unsupported()
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").SchemaVersion(2));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ArchiveIssueCode.UnsupportedSchema, issue.Code);
        Assert.Contains("newer XIVault", issue.Message);
    }

    [Fact]
    public void Archive_without_a_manifest_is_rejected()
    {
        Directory.CreateDirectory(_env.Root);
        using (var zip = System.IO.Compression.ZipFile.Open(ArchivePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("payload/dalamudConfig.json");
        }

        var result = _validator.Validate(ArchivePath, verifyContents: true, TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveIssueCode.MissingManifest, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_an_invalid_archive()
    {
        Directory.CreateDirectory(_env.Root);
        File.WriteAllBytes(ArchivePath, [0x4D, 0x5A, 0x90, 0x00, 0x03]);

        var result = _validator.Validate(ArchivePath, verifyContents: true, TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveIssueCode.InvalidArchive, Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("4611686018427387904", "4611686018427387904")]
    [InlineData("9223372036854775807", "1")]
    [InlineData("3000000000", "0")]
    public void Absurd_declared_sizes_are_too_large_not_a_crash(string first, string second)
    {
        var result = Validate(new ArchiveBuilder().File("payload/dalamudConfig.json", "{}").RawManifest($$"""
            {
              "schemaVersion": 1, "xivaultVersion": "0.1.0", "createdAtUtc": "2026-09-28T18:38:00Z", "backupType": "manual",
              "source": {}, "contents": { "pluginConfigs": true }, "statistics": {},
              "files": [
                { "path": "payload/pluginConfigs/A.json", "size": {{first}}, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" },
                { "path": "payload/pluginConfigs/B.json", "size": {{second}}, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
              ]
            }
            """));

        Assert.Equal(ArchiveIssueCode.TooLarge, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Listed_files_missing_from_the_archive_are_rejected()
    {
        var result = Validate(new ArchiveBuilder()
            .File("payload/dalamudConfig.json", "{}")
            .RawManifest("""
                {
                  "schemaVersion": 1, "xivaultVersion": "0.1.0", "createdAtUtc": "2026-09-28T18:38:00Z", "backupType": "manual",
                  "source": {}, "contents": { "dalamudVfs": true }, "statistics": { "fileCount": 1, "totalBytes": 1 },
                  "files": [ { "path": "payload/dalamudVfs.db", "size": 1, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ]
                }
                """));

        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.MissingFile);
        Assert.Contains(result.Issues, issue => issue.Code == ArchiveIssueCode.UnexpectedEntry);
    }
}
