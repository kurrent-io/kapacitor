namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Why this machine has stopped listening, as the server's own vocabulary. The browser renders a
/// different remedy per member and <b>the two are not interchangeable</b>: telling someone who chose to
/// carry on here to run setup again would make them restart a run that is mid-flight.
/// </summary>
public static class FirstRunRelinquishReasons {
    /// <summary>The user pressed a key to carry on in the terminal. Everything the browser settled comes
    /// with us, so the page has nothing left to offer and nothing to warn about.</summary>
    public const string Handover = "handover";

    /// <summary>Anything else: the backstop elapsed, the leg failed, or the process was interrupted. The
    /// remedy the browser states is <c>kcap setup</c> on this machine.</summary>
    public const string Stopped = "stopped";
}
