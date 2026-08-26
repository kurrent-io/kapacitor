namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Hands a URL to a browser. Injected rather than defaulted everywhere it is used: the only
/// sensible default is the real one, so a caller that forgets to say otherwise opens a page — and
/// under test that page opens on the developer's machine.
/// </summary>
public interface IBrowserLauncher {
    /// <summary>
    /// <para><b>A true answer is not confirmation a human saw a page</b> - it says a handler was
    /// launched. A <b>false</b> answer is worth acting on, though: there is no browser on this
    /// machine, so the user's browser is on another one, and any 127.0.0.1 callback this process is
    /// listening on is unreachable from it.</para>
    /// </summary>
    bool TryOpen(string url);
}
