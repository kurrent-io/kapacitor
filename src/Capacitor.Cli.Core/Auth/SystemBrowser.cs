using System.Diagnostics;

namespace Capacitor.Cli.Core.Auth;

/// <summary>Hands the URL to whatever the OS considers the browser.</summary>
public sealed class SystemBrowser : IBrowserLauncher {
    public static readonly SystemBrowser Instance = new();

    public bool TryOpen(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            return true;
        } catch {
            return false;
        }
    }
}
