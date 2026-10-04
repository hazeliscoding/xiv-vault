namespace XIVault.Core.Platform;

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
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
