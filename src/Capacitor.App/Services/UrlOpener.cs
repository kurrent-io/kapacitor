using System.Diagnostics;

namespace Capacitor.App.Services;

public interface IUrlOpener {
    void Open(string url);
}

/// UseShellExecute routes the URL through the OS shell handler (macOS `open`, Windows
/// ShellExecute, Linux xdg-open) instead of attempting to launch it as an executable.
public sealed class ShellUrlOpener : IUrlOpener {
    public void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
