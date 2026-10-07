namespace XivVault.Core.Platform;

public interface IFileAvailability
{
    /// <summary>
    /// True when the file's contents are in the cloud and not on this PC, as OneDrive Files
    /// On-Demand keeps them. Reading such a file downloads it; asking this never does.
    /// </summary>
    bool IsOnlineOnly(string path);
}

public sealed class WindowsFileAvailability : IFileAvailability
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, which .NET doesn't
    // name. Cloud sync apps set them on placeholders; Offline is the older flag for the same thing.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    private const FileAttributes NotLocal = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline;

    public bool IsOnlineOnly(string path)
    {
        try
        {
            return (File.GetAttributes(path) & NotLocal) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
