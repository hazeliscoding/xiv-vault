namespace XivVault.Core.Platform;

/// <summary>
/// Small settings files (config.json, state.json) that the app, the CLI and the scheduled task
/// all read and write.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes through a temp file and a rename, so a crash never leaves half a file. The temp name is
    /// unique, so two processes saving at once can't trip over each other's temp file.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, contents);

            // Another reader can hold the file for a moment; the rename waits for it instead of failing.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Reads with full sharing, so a concurrent rename-over by a writer is never blocked.</summary>
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
