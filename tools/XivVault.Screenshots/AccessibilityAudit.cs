using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace XivVault.Screenshots;

/// <summary>
/// What a keyboard user and a screen reader get from one screen: the Tab order with each stop's
/// name, role and whether focus shows, and the UI Automation tree Narrator reads. Lines starting
/// with "!" are problems.
/// </summary>
internal static class AccessibilityAudit
{
    public static string Report(Window window, Action settle)
    {
        var lines = new List<string> { "TAB ORDER" };
        var focusManager = window.FocusManager!;
        focusManager.Focus(null);
        settle();
        var seen = new List<Control>();
        for (var press = 0; press < 150; press++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            settle();
            if (focusManager.GetFocusedElement() is not Control focused)
            {
                lines.Add("! Tab left the window or focused nothing");
                break;
            }

            if (seen.Contains(focused))
            {
                break;
            }

            seen.Add(focused);
            var withFocus = window.CaptureRenderedFrame();
            focusManager.Focus(null);
            settle();
            var without = window.CaptureRenderedFrame();
            var shows = Differs(withFocus, without, Area(focused, window));
            focused.Focus(NavigationMethod.Tab);
            settle();
            lines.Add($"{(shows ? " " : "!")} {Describe(focused)}{(shows ? "" : "  (no visible focus)")}");
            if (focused is ListBoxItem item)
            {
                // Tab stops once in a list; the arrow keys must reach the other items.
                var reached = new HashSet<Control> { item };
                foreach (var key in new[] { PhysicalKey.ArrowRight, PhysicalKey.ArrowDown, PhysicalKey.ArrowLeft, PhysicalKey.ArrowUp })
                {
                    for (var step = 0; step < 12; step++)
                    {
                        window.KeyPressQwerty(key, RawInputModifiers.None);
                        window.KeyReleaseQwerty(key, RawInputModifiers.None);
                        settle();
                        if (focusManager.GetFocusedElement() is ListBoxItem next)
                        {
                            reached.Add(next);
                        }
                    }
                }

                var all = item.GetVisualAncestors().OfType<ListBox>().First().GetRealizedContainers().Count();
                lines.Add($"{(reached.Count < all ? "!" : " ")}   arrow keys reach {reached.Count} of {all} items");
                seen.AddRange(reached);
                item.Focus(NavigationMethod.Tab);
                settle();
            }
        }

        lines.Add("");
        lines.Add("FOCUSABLE BUT NOT REACHED BY TAB, OR CLICKABLE BUT NOT FOCUSABLE");
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && OnScreen(c, window)))
        {
            if (control.Focusable && !seen.Contains(control) && ControlAutomationPeer.CreatePeerForElement(control).IsKeyboardFocusable())
            {
                lines.Add($"? {Describe(control)}");
            }
            else if (!control.Focusable && control.Cursor?.ToString() == "Hand" && !control.GetVisualAncestors().OfType<Control>().Any(a => a.Focusable && a.Cursor?.ToString() == "Hand"))
            {
                lines.Add($"! clickable, not focusable: {Describe(control)}");
            }
        }

        lines.Add("");
        lines.Add("AUTOMATION TREE (what Narrator reads)");
        Walk(ControlAutomationPeer.CreatePeerForElement(window), 0, lines);
        focusManager.Focus(null);
        settle();
        return string.Join(Environment.NewLine, lines);
    }

    private static void Walk(AutomationPeer peer, int depth, List<string> lines)
    {
        foreach (var child in peer.GetChildren())
        {
            if (child.IsOffscreen())
            {
                continue;
            }

            var type = child.GetAutomationControlType();
            var name = child.GetName();
            var unnamed = string.IsNullOrWhiteSpace(name) && type is not (AutomationControlType.Group or AutomationControlType.Pane or AutomationControlType.Custom or AutomationControlType.Text or AutomationControlType.ScrollBar or AutomationControlType.Separator or AutomationControlType.Thumb or AutomationControlType.ScrollViewer);
            if (child.IsControlElement())
            {
                lines.Add($"{(unnamed ? "!" : " ")} {new string(' ', depth * 2)}{type} \"{name}\"{(child.IsEnabled() ? "" : " [disabled]")}{(unnamed ? "  (no name)" : "")}");
            }

            Walk(child, child.IsControlElement() ? depth + 1 : depth, lines);
        }
    }

    private static string Describe(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        var name = peer.GetName();
        return $"{peer.GetAutomationControlType()} \"{(string.IsNullOrWhiteSpace(name) ? "(no name)" : name)}\" <{control.GetType().Name}{(control.Name is { } id ? "#" + id : "")}>";
    }

    private static bool OnScreen(Control control, Window window) => Area(control, window) is { Width: > 0, Height: > 0 };

    /// <summary>The control's bounds in window pixels, with room for a focus ring drawn outside it.</summary>
    private static PixelRect Area(Control control, Window window)
    {
        if (control.TranslatePoint(default, window) is not { } origin)
        {
            return default;
        }

        var rect = new Rect(origin, control.Bounds.Size).Inflate(4).Intersect(new Rect(window.ClientSize));
        return PixelRect.FromRect(rect, 1);
    }

    private static bool Differs(WriteableBitmap? a, WriteableBitmap? b, PixelRect area)
    {
        if (a is null || b is null || area.Width <= 0 || area.Height <= 0)
        {
            return false;
        }

        var stride = area.Width * 4;
        var first = new byte[stride * area.Height];
        var second = new byte[first.Length];
        Copy(a, area, first, stride);
        Copy(b, area, second, stride);
        return !first.AsSpan().SequenceEqual(second);
    }

    private static void Copy(Bitmap bitmap, PixelRect area, byte[] buffer, int stride)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(area, handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }
    }
}
