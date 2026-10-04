namespace XIVault.Desktop.Services;

/// <summary>A confirmation the user must answer, shown as an in-window modal.</summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmText,
    string CancelText = "Cancel")
{
    public string? Detail { get; init; }

    public string? Note { get; init; }

    public bool Danger { get; init; }

    public string? ConfirmIcon { get; init; }
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(ConfirmRequest request);
}

public interface IShellService
{
    /// <summary>Opens File Explorer with the file selected.</summary>
    void RevealInExplorer(string path);

    /// <summary>Starts a program, such as XIVLauncher. Returns false if it could not be started.</summary>
    bool Launch(string executable);

    void OpenUrl(string url);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public interface IFilePicker
{
    Task<string?> PickFolderAsync(string title, string? startFolder);

    Task<string?> PickBackupFileAsync(string? startFolder);
}

/// <summary>Runs progress updates on the UI thread. Tests run them inline.</summary>
public interface IUiThread
{
    void Post(Action action);
}

public enum AppPage
{
    Overview,
    Backups,
    Restore,
    Schedule,
    Diagnostics,
    Settings,
}

public interface INavigator
{
    void Navigate(AppPage page);

    /// <summary>Opens the restore wizard, optionally with a backup already chosen.</summary>
    void StartRestore(string? backupPath = null);
}

public sealed class Navigator : INavigator
{
    public event Action<AppPage, string?>? Navigated;

    public void Navigate(AppPage page) => Navigated?.Invoke(page, null);

    public void StartRestore(string? backupPath = null) => Navigated?.Invoke(AppPage.Restore, backupPath);
}
