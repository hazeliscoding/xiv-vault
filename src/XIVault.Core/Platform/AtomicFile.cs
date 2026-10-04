namespace XIVault.Core.Platform;

internal static class AtomicFile
{
    /// <summary>Writes through a temp file and a rename, so a crash never leaves half a file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
