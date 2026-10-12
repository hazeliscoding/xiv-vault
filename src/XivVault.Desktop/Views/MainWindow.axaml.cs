using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XivVault.Desktop.ViewModels;

namespace XivVault.Desktop.Views;

/// <summary>Window chrome only: caption buttons, the modal's Escape and focus, scroll reset between pages.</summary>
public partial class MainWindow : Window
{
    private IInputElement? _focusBeforeDialog;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
                    {
                        PageScroller.Offset = default;
                    }
                };
            }
        };
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.Dialogs.Active?.Close(false);

    // The confirmation's safe choice takes focus when it opens, so a stray Enter never confirms,
    // and focus goes back to where it was once the page behind is enabled again.
    private void OnDialogOpened(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _focusBeforeDialog = FocusManager?.GetFocusedElement();
        (sender as Control)?.Focus(NavigationMethod.Tab);
    }

    private void OnDialogClosed(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var previous = _focusBeforeDialog as Control;
        _focusBeforeDialog = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (previous is { IsEffectivelyVisible: true } && TopLevel.GetTopLevel(previous) == this)
            {
                previous.Focus(NavigationMethod.Tab);
            }
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Dialogs.Active: { } dialog })
        {
            dialog.Close(false);
            e.Handled = true;
        }
    }
}
