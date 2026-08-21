namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// What the browser leg produced.
///
/// <para><b>Only <see cref="Finished"/> is a success, and none of the rest is a failure of setup.</b>
/// The leg is additive: sign-in has already happened by the time it runs, so every way it can end
/// leaves a machine that can be configured from the terminal. <see cref="Unavailable"/> in particular
/// is the ordinary case — the tenant does not serve the flow — and says nothing to the user.</para>
/// </summary>
public abstract record FirstRunFlowResult {
    /// <summary>The browser reached the end. <paramref name="View"/> is the last state polled.</summary>
    public sealed record Finished(FirstRunFlowResponse View) : FirstRunFlowResult;

    /// <summary>The flow aged out before it was finished. Re-runnable.</summary>
    public sealed record Expired : FirstRunFlowResult;

    /// <summary>The local budget ran out with the flow unfinished — usually a closed tab.
    /// <paramref name="View"/> is the last state seen, or null if none ever was.</summary>
    public sealed record Abandoned(FirstRunFlowResponse? View) : FirstRunFlowResult;

    /// <summary>The user pressed a key to stop waiting. Distinct from <see cref="Abandoned"/> because
    /// it is a choice rather than a timeout, and reporting a chosen thing as a warning is how a CLI
    /// teaches people to ignore its warnings.</summary>
    public sealed record Dismissed(FirstRunFlowResponse? View) : FirstRunFlowResult;

    /// <summary>This tenant does not serve the flow. Not an error, and not worth a word to the
    /// user: the routes are mapped only when <c>Features:FirstRun</c> is on, so their absence is a
    /// fact the CLI can observe rather than a version it has to guess at.</summary>
    public sealed record Unavailable : FirstRunFlowResult;

    /// <summary>Too many flows created recently. <paramref name="RetryAfter"/> is what the server
    /// asked for, which is far longer than an interactive setup can wait.</summary>
    public sealed record RateLimited(TimeSpan RetryAfter) : FirstRunFlowResult;

    /// <summary>Something went wrong that is worth one line to the user before setup carries on.</summary>
    public sealed record Failed(string Message) : FirstRunFlowResult;
}
