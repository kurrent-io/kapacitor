using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// The one "empty chrome space drags the window" rule: the client area extends into the title
/// bar, so each surface's top strip must move the window the way the system bar used to. Buttons
/// in a strip mark their presses handled, so a wired handler only fires on the blank stretch.
public static class WindowChrome {
    public static void BeginDrag(Visual host, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(host).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(host) is Window window)
            window.BeginMoveDrag(e);
    }
}
