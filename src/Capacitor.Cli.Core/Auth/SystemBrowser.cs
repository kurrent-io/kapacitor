using System.Diagnostics;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Hands a URL to whatever the OS considers the browser.
///
/// <para><b>Best-effort by design, and every caller must print the URL anyway.</b> There is no
/// reliable way to learn that the browser opened — <c>UseShellExecute</c> succeeding says a handler
/// was launched, not that a human saw a page — so a flow that treats this as confirmation strands
/// anyone on a headless box, a broken desktop session, or an SSH forward.</para>
/// </summary>
public static class SystemBrowser {
    public static void Open(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch {
            // Swallowed: the fallback link is already on screen, which is the actual remedy.
        }
    }
}
