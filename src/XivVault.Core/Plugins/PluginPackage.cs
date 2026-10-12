using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XivVault.Core.Backup;

namespace XivVault.Core.Plugins;

internal static class PluginPackage
{
    internal const int MaxEntries = 20_000;
    internal const long MaxBytes = 1L << 30;
    private const int MaxManifestBytes = 1 << 20;

    /// <summary>
    /// Checks a downloaded plugin as Dalamud's installer does, and stricter, then extracts it into
    /// <paramref name="stagingFolder"/> with the manifest Dalamud would write, carrying the backup's ID.
    /// Returns <see cref="PluginInstallOutcome.Installed"/> when it is staged, or
    /// <see cref="PluginInstallOutcome.PackageRefused"/> after removing anything it wrote.
    /// </summary>
    public static PluginInstallOutcome Stage(string zipPath, PlannedInstall plan, Version dalamudVersion, string stagingFolder) =>
        Stage(zipPath, plan, dalamudVersion, stagingFolder, MaxEntries, MaxBytes);

    // The caps are parameters so tests can use small ones.
    internal static PluginInstallOutcome Stage(string zipPath, PlannedInstall plan, Version dalamudVersion, string stagingFolder, int maxEntries, long maxBytes)
    {
        // Never write into, or remove, a folder something else made.
        if (Path.Exists(stagingFolder))
        {
            throw new IOException("The plugin's work folder already exists.");
        }

        Directory.CreateDirectory(stagingFolder);
        try
        {
            if (TryStage(zipPath, plan, dalamudVersion, stagingFolder, maxEntries, maxBytes))
            {
                return PluginInstallOutcome.Installed;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // A broken zip or manifest, or one over a cap, is refused like any other bad package.
        }
        catch
        {
            TryDelete(stagingFolder);
            throw;
        }

        TryDelete(stagingFolder);
        return PluginInstallOutcome.PackageRefused;
    }

    private static bool TryStage(string zipPath, PlannedInstall plan, Version dalamudVersion, string stagingFolder, int maxEntries, long maxBytes)
    {
        // The manifest and DLL must sit at the zip root, so the name must be one folder name.
        var name = plan.Entry.InternalName;
        if (!ArchivePaths.IsSafe(name) || name.Contains('/'))
        {
            return false;
        }

        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > maxEntries)
        {
            return false;
        }

        // Windows reads a backslash as a separator and matches names without case, so entries are read that way.
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!ArchivePaths.IsSafe(path))
            {
                return false;
            }

            if (ArchivePaths.IsDirectoryEntry(path))
            {
                continue;
            }

            if (!files.TryAdd(path, entry))
            {
                return false;
            }

            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                folders.Add(path[..slash]);
            }
        }

        // Dalamud only finds a missing DLL in game; a file where another entry needs a folder can't be written.
        if (files.Keys.Any(folders.Contains)
            || !files.TryGetValue(name + ".json", out var manifestEntry)
            || !files.ContainsKey(name + ".dll"))
        {
            return false;
        }

        var api = dalamudVersion.Major;
        using var packaged = ReadManifest(manifestEntry);
        if (!IsInstallable(PluginManifestFile.Read(packaged.RootElement, api), plan, dalamudVersion))
        {
            return false;
        }

        var manifest = PluginManifestFile.ForInstall(
            packaged.RootElement, plan.Plugin.WorkingPluginId, plan.UseTesting, plan.RepositoryUrl ?? "OFFICIAL", plan.Entry.Dip17Channel, api);

        // Nothing is ever run: every file is only copied, counting what is read since a zip's recorded sizes can lie.
        long total = 0;
        foreach (var (path, entry) in files)
        {
            var target = ArchivePaths.ResolveUnder(stagingFolder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                CopyBounded(input, output, maxBytes, ref total);
            }

            // Dalamud's extraction keeps each file's time from the zip too; nothing reads it.
            File.SetLastWriteTimeUtc(target, entry.LastWriteTime.UtcDateTime);
        }

        // Last, over the packaged copy, as Dalamud's installer saves it.
        File.WriteAllBytes(ArchivePaths.ResolveUnder(stagingFolder, manifestEntry.FullName.Replace('\\', '/')), manifest);
        return true;
    }

    // As Dalamud reads it: File.ReadAllText's encoding detection, then JSON as Newtonsoft accepts it.
    private static JsonDocument ReadManifest(ZipArchiveEntry entry)
    {
        using var bytes = new MemoryStream();
        long size = 0;
        using (var input = entry.Open())
        {
            CopyBounded(input, bytes, MaxManifestBytes, ref size);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    // Dalamud's installer checks the name, version and ID; the rest it only finds out when the plugin fails to load.
    private static bool IsInstallable(Dictionary<string, object?> manifest, PlannedInstall plan, Version dalamudVersion) =>
        manifest["InternalName"] as string == plan.Entry.InternalName
        && manifest["AssemblyVersion"] as Version == plan.Version
        && (Guid)manifest["WorkingPluginId"]! == Guid.Empty
        && !string.IsNullOrWhiteSpace(manifest["Name"] as string)
        && (int)manifest["DalamudApiLevel"]! == dalamudVersion.Major
        && !(manifest["MinimumDalamudVersion"] is Version minimum && minimum > dalamudVersion)
        && string.Equals(manifest["ApplicableVersion"] as string, "any", StringComparison.OrdinalIgnoreCase);

    private static void CopyBounded(Stream input, Stream output, long limit, ref long total)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("The plugin holds more data than XIV Vault accepts.");
            }

            output.Write(buffer, 0, read);
        }
    }

    // A file still held open, by an antivirus scan say, stays until the work folder is cleaned up.
    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
