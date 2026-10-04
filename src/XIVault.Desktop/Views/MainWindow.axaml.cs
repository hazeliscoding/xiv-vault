using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XIVault.Desktop.ViewModels;

namespace XIVault.Desktop.Views;

/// <summary>Window chrome only: caption buttons, Escape for the modal, scroll reset between pages.</summary>
public partial class MainWindow : Window
{
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Dialogs.Active: { } dialog })
        {
            dialog.Close(false);
            e.Handled = true;
        }
    }
}
