using System.Diagnostics;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Hands a URL to whatever the OS considers the browser.
///
/// <para><b>A true answer is not confirmation a human saw a page</b> - <c>UseShellExecute</c>
/// succeeding says a handler was launched. A <b>false</b> answer is worth acting on, though: there is
/// no browser on this machine, so the user's browser is on another one, and any 127.0.0.1 callback
/// this process is listening on is unreachable from it.</para>
/// </summary>
public static class SystemBrowser {
    public static bool TryOpen(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            return true;
        } catch {
            return false;
        }
    }
}
