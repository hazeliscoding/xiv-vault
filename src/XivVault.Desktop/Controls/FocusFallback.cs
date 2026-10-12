using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XivVault.Desktop.Controls;

/// <summary>
/// Where focus goes when the focused control disappears, as when Back Up Now turns into progress
/// or the restore wizard moves on a step. Without a target, focus falls out of the window and the
/// next Tab starts again at the top. A target that isn't shown yet gets focus once it appears.
/// </summary>
public static class FocusFallback
{
    public static readonly AttachedProperty<bool> IsTargetProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsTarget", typeof(FocusFallback));

    // Keyboard users get the focus ring back; someone who clicked doesn't.
    private static NavigationMethod _lastMethod = NavigationMethod.Tab;

    static FocusFallback()
    {
        InputElement.GotFocusEvent.AddClassHandler<InputElement>((_, e) => _lastMethod = e.NavigationMethod);
        InputElement.LostFocusEvent.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.NewFocusedElement is null && TopLevel.GetTopLevel(element) is { } top)
            {
                // After the change that hid the control has been laid out, and only if it did go.
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (!element.IsEffectivelyVisible || !element.IsEffectivelyEnabled || TopLevel.GetTopLevel(element) != top)
                        {
                            Recover(top);
                        }
                    },
                    DispatcherPriority.Background);
            }
        });
    }

    public static bool GetIsTarget(Control control) => control.GetValue(IsTargetProperty);

    public static void SetIsTarget(Control control, bool value) => control.SetValue(IsTargetProperty, value);

    private static void Recover(TopLevel top)
    {
        if (!TryFocus(top))
        {
            void Retry(object? sender, EventArgs e)
            {
                if (TryFocus(top))
                {
                    top.LayoutUpdated -= Retry;
                }
            }

            top.LayoutUpdated += Retry;
        }
    }

    /// <summary>True once something has focus again, whether a target or something the user chose.</summary>
    private static bool TryFocus(TopLevel top)
    {
        if (top.FocusManager?.GetFocusedElement() is not null)
        {
            return true;
        }

        var target = top.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => GetIsTarget(control) && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
        return target?.Focus(_lastMethod) == true;
    }
}
