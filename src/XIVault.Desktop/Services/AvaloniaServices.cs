using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace XIVault.Desktop.Services;

public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

public sealed class WindowsShellService : IShellService
{
    public void RevealInExplorer(string path)
    {
        // explorer.exe takes "/select,<path>" as one argument; quoting keeps paths with commas intact.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    public bool Launch(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>Clipboard and file pickers need the window; it is attached once it exists.</summary>
public sealed class WindowServices : IClipboardService, IFilePicker
{
    public TopLevel? TopLevel { get; set; }

    public async Task SetTextAsync(string text)
    {
        if (TopLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder);
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickBackupFileAsync(string? startFolder)
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = "Choose a XIV Vault backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("XIV Vault backup") { Patterns = ["*.zip"] }],
        };
        if (startFolder is not null && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder);
        }

        var files = await storage.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
