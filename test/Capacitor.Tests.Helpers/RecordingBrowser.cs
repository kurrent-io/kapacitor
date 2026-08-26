using Capacitor.Cli.Core.Auth;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// The browser every test hands to an auth flow. <see cref="SystemBrowser"/> would open the page on
/// the developer's machine — which is how a scripted https://signin.example/device ended up in real
/// browser tabs — so this records the URL instead.
/// </summary>
/// <param name="opens">
/// Reports success by default, so the browser-opened branch is still the one exercised. False drives
/// the no-browser routes: the device grant's "open this yourself", and the loopback flow's refusal.
/// </param>
public sealed class RecordingBrowser(bool opens = true) : IBrowserLauncher {
    public List<string> Urls { get; } = [];

    public bool TryOpen(string url) {
        Urls.Add(url);

        return opens;
    }
}
