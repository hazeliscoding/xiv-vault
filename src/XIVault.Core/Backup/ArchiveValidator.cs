using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XIVault.Core.Serialization;

namespace XIVault.Core.Backup;

public enum ArchiveIssueCode
{
    InvalidArchive,
    MissingManifest,
    MalformedManifest,
    UnsupportedSchema,
    UnsafePath,
    NotAllowlisted,
    UnexpectedEntry,
    DuplicateEntry,
    MissingFile,
    SizeMismatch,
    ChecksumMismatch,
    TooLarge,
}

public sealed record ArchiveIssue(ArchiveIssueCode Code, string Message, string? EntryPath = null);

public sealed record ArchiveValidation(BackupManifest? Manifest, IReadOnlyList<ArchiveIssue> Issues, string? ArchiveSha256)
{
    public bool IsValid => Manifest is not null && Issues.Count == 0;

    public string Summary => Issues.Count == 0 ? "Verified" : Issues[0].Message;
}

/// <summary>
/// Decides whether an archive can be trusted. Everything restore does depends on this passing,
/// so it is strict: anything it does not recognize is a failure, not a warning.
/// </summary>
public sealed class ArchiveValidator
{
    public const long MaxManifestBytes = 16 * 1024 * 1024;

    // Real Dalamud configurations are tens of megabytes. The cap stops a crafted archive from
    // claiming terabytes and filling the disk during extraction.
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    public ArchiveValidation Validate(string archivePath, bool verifyContents, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var validation = Validate(stream, verifyContents, cancellationToken);
            if (!verifyContents)
            {
                return validation;
            }

            stream.Position = 0;
            return validation with { ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ArchiveIssueCode.InvalidArchive, $"The archive could not be read: {ex.Message}");
        }
    }

    public ArchiveValidation Validate(Stream archive, bool verifyContents, CancellationToken cancellationToken = default)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return Fail(ArchiveIssueCode.InvalidArchive, $"The file is not a valid ZIP archive ({ex.Message}).");
        }

        using (zip)
        {
            try
            {
                return Validate(zip, verifyContents, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                return Fail(ArchiveIssueCode.InvalidArchive, $"The archive is damaged ({ex.Message}).");
            }
        }
    }

    /// <summary>Reads and checks the manifest only. Cheap enough to run for every archive in a folder.</summary>
    public static (BackupManifest? Manifest, ArchiveIssue? Issue) ReadManifest(ZipArchive zip)
    {
        var entries = zip.Entries.Where(entry => entry.FullName == BackupAllowlist.ManifestEntryName).ToList();
        if (entries.Count == 0)
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.MissingManifest, "The archive has no manifest.json, so it was not made by XIVault."));
        }

        if (entries.Count > 1)
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.DuplicateEntry, "The archive has more than one manifest.json.", BackupAllowlist.ManifestEntryName));
        }

        var entry = entries[0];
        if (entry.Length > MaxManifestBytes)
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.TooLarge, "manifest.json is too large.", entry.FullName));
        }

        BackupManifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize(stream, XivaultJsonContext.Default.BackupManifest);
        }
        catch (JsonException ex)
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.MalformedManifest, $"manifest.json is not valid ({ex.Message})."));
        }

        if (manifest is null)
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.MalformedManifest, "manifest.json is empty."));
        }

        if (manifest.SchemaVersion < 1)
        {
            return (null, new ArchiveIssue(
                ArchiveIssueCode.UnsupportedSchema,
                "manifest.json has no supported schemaVersion. The archive predates XIVault 0.1 or was not made by XIVault."));
        }

        if (manifest.SchemaVersion > BackupManifest.CurrentSchemaVersion)
        {
            return (null, new ArchiveIssue(
                ArchiveIssueCode.UnsupportedSchema,
                $"The backup uses format version {manifest.SchemaVersion}, made by a newer XIVault. Update XIVault to restore it."));
        }

        // System.Text.Json assigns an explicit null over the initializer defaults.
        if (manifest.Files is null || manifest.Contents is null || manifest.Statistics is null || manifest.Source is null
            || manifest.CreatedAtUtc == default || !Enum.IsDefined(manifest.BackupType)
            || manifest.Files.Any(file => file is null || string.IsNullOrEmpty(file.Path) || file.Sha256 is null))
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.MalformedManifest, "manifest.json is missing required fields."));
        }

        // The summary fields are shown to people before a restore, so they must describe the files
        // that would actually be written, not whatever the manifest claims.
        if (!SummaryMatchesFiles(manifest))
        {
            return (null, new ArchiveIssue(ArchiveIssueCode.MalformedManifest, "manifest.json describes different contents than the files it lists."));
        }

        return (manifest, null);
    }

    private static bool SummaryMatchesFiles(BackupManifest manifest)
    {
        var paths = manifest.Files.Select(file => file.Path).ToList();
        bool Has(string archivePath) => paths.Any(path => string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase));
        var pluginPrefix = BackupAllowlist.PayloadPrefix + BackupAllowlist.PluginConfigsDirectory + "/";
        var contents = manifest.Contents;
        var statistics = manifest.Statistics;
        return contents.PluginConfigs == paths.Any(path => path.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
            && contents.DalamudConfig == Has(BackupAllowlist.ArchivePathFor(PortableItem.DalamudConfig))
            && contents.DalamudVfs == Has(BackupAllowlist.ArchivePathFor(PortableItem.DalamudVfs))
            && contents.DalamudUi == Has(BackupAllowlist.ArchivePathFor(PortableItem.DalamudUi))
            && statistics.FileCount == paths.Count
            && statistics.PluginConfigCount == BackupAllowlist.PluginNames(paths).Count
            && statistics.TotalBytes == manifest.Files.Sum(file => file.Size);
    }

    private static ArchiveValidation Validate(ZipArchive zip, bool verifyContents, CancellationToken cancellationToken)
    {
        var (manifest, manifestIssue) = ReadManifest(zip);
        if (manifest is null)
        {
            return new ArchiveValidation(null, [manifestIssue!], null);
        }

        var issues = new List<ArchiveIssue>();
        var expected = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.MalformedManifest, "manifest.json lists a file without a path."));
                continue;
            }

            if (!ArchivePaths.IsSafe(file.Path) || ArchivePaths.IsDirectoryEntry(file.Path))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.UnsafePath, $"manifest.json lists an unsafe path: {file.Path}", file.Path));
                continue;
            }

            if (!BackupAllowlist.TryClassify(file.Path, out _, out _))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.NotAllowlisted, $"manifest.json lists a file XIVault never backs up: {file.Path}", file.Path));
                continue;
            }

            if (file.Size < 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.MalformedManifest, $"manifest.json has an invalid size or hash for {file.Path}.", file.Path));
                continue;
            }

            if (!expected.TryAdd(file.Path, file))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.DuplicateEntry, $"manifest.json lists {file.Path} twice.", file.Path));
            }

            declaredTotal += file.Size;
        }

        if (declaredTotal > MaxTotalBytes)
        {
            issues.Add(new ArchiveIssue(ArchiveIssueCode.TooLarge, "The backup claims to hold more data than any Dalamud configuration could."));
        }

        var seen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name == BackupAllowlist.ManifestEntryName)
            {
                continue;
            }

            if (!ArchivePaths.IsSafe(name))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.UnsafePath, $"The archive contains an unsafe path: {name}", name));
                continue;
            }

            if (ArchivePaths.IsDirectoryEntry(name))
            {
                // Folder entries carry no data. Some ZIP tools add them; they are fine inside payload/.
                if (!name.StartsWith(BackupAllowlist.PayloadPrefix, StringComparison.Ordinal))
                {
                    issues.Add(new ArchiveIssue(ArchiveIssueCode.UnexpectedEntry, $"The archive contains an unexpected folder: {name}", name));
                }

                continue;
            }

            if (!seen.TryAdd(name, entry))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.DuplicateEntry, $"The archive contains {name} twice.", name));
                continue;
            }

            if (!expected.ContainsKey(name))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.UnexpectedEntry, $"The archive contains a file the manifest does not list: {name}", name));
            }
        }

        foreach (var (path, file) in expected)
        {
            if (!seen.TryGetValue(path, out var entry))
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.MissingFile, $"{path} is listed in the manifest but missing from the archive.", path));
                continue;
            }

            if (entry.Length != file.Size)
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.SizeMismatch, $"{path} does not have the size recorded in the manifest.", path));
            }
        }

        if (issues.Count > 0 || !verifyContents)
        {
            return new ArchiveValidation(manifest, issues, null);
        }

        foreach (var (path, file) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var content = seen[path].Open();
            var actual = HashBounded(content, file.Size, out var overflow);
            if (overflow)
            {
                issues.Add(new ArchiveIssue(ArchiveIssueCode.SizeMismatch, $"{path} holds more data than the manifest records.", path));
            }
            else if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ArchiveIssue(
                    ArchiveIssueCode.ChecksumMismatch,
                    $"{path} does not match its recorded hash (expected {Short(file.Sha256)}, got {Short(actual)}).",
                    path));
            }
        }

        return new ArchiveValidation(manifest, issues, null);
    }

    /// <summary>Hashes at most <paramref name="limit"/> bytes, flagging a stream that keeps going.</summary>
    internal static string HashBounded(Stream stream, long limit, out bool overflow)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        overflow = false;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                overflow = true;
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string Short(string sha256) =>
        sha256.Length < 8 ? sha256 : $"{sha256[..4]}…{sha256[^4..]}";

    private static ArchiveValidation Fail(ArchiveIssueCode code, string message) =>
        new(null, [new ArchiveIssue(code, message)], null);
}
