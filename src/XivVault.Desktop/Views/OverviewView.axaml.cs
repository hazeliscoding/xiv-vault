using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XivVault.Desktop.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    // Tooltips only open on hover; pressing the "?" with the keyboard opens it too.
    private void OnExplainClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            ToolTip.SetIsOpen(control, !ToolTip.GetIsOpen(control));
        }
    }
}
