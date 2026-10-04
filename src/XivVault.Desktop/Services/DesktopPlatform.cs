using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XivVault.Core.Scheduling;

namespace XivVault.Desktop.Services;

public interface IMotionSettings
{
    /// <summary>True when Windows animations are turned off; the app then shows no motion.</summary>
    bool ReduceMotion { get; }
}

public sealed partial class WindowsMotionSettings : IMotionSettings
{
    private const uint GetClientAreaAnimation = 0x1042;

    public bool ReduceMotion { get; } = Read();

    private static bool Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // SPI_GETCLIENTAREAANIMATION is the "Animation effects" switch in Windows accessibility settings.
        var enabled = 1;
        return SystemParametersInfo(GetClientAreaAnimation, 0, ref enabled, 0) && enabled == 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}

/// <summary>What the Windows task runs when the desktop app sets the schedule: itself, without a window.</summary>
public interface ISchedulerTarget
{
    ScheduledCommand Command { get; }
}

public sealed class DesktopSchedulerTarget : ISchedulerTarget
{
    public const string ScheduledBackupArgument = "--scheduled-backup";

    public ScheduledCommand Command => ScheduledCommand.ForCurrentProcess(ScheduledBackupArgument);
}

/// <summary>The in-window confirmation modal. The window shows whatever <see cref="Active"/> holds.</summary>
public sealed partial class OverlayDialogService : ObservableObject, IDialogService
{
    [ObservableProperty]
    public partial ConfirmDialog? Active { get; private set; }

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Active?.Close(false);
        var dialog = new ConfirmDialog(request, () => Active = null);
        Active = dialog;
        return dialog.Result;
    }
}

public sealed partial class ConfirmDialog(ConfirmRequest request, Action onClosed) : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmRequest Request { get; } = request;

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    public void Close(bool confirmed)
    {
        if (_result.TrySetResult(confirmed))
        {
            onClosed();
        }
    }
}
