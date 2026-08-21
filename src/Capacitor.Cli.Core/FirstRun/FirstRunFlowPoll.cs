namespace Capacitor.Cli.Core.FirstRun;

/// <summary>What one poll of the flow means for the loop.</summary>
public enum FirstRunPollVerdict {
    /// <summary>A readable state arrived. Whether it ends the loop is
    /// <see cref="FirstRunFlowOutcomes.IsFinished"/>'s question, not this one's.</summary>
    State,

    /// <summary>Past the flow's TTL. Terminal, and re-runnable.</summary>
    Expired,

    /// <summary>No such flow, or not ours — the server refuses to say which. Terminal.</summary>
    Gone,

    /// <summary>The bearer is no longer accepted. Terminal, and a different remedy from
    /// <see cref="Gone"/>.</summary>
    Unauthenticated,

    /// <summary>Polled too fast. Back off and keep waiting.</summary>
    SlowDown,

    /// <summary>A blip the next tick recovers from.</summary>
    Wait
}

/// <summary>
/// The pure decision behind the flow poll, extracted so every branch is unit-tested without a server.
/// On the same reasoning the retired pairing's was: a poll treating every unexpected response as
/// "keep waiting" spins silently until timeout, which is indistinguishable to the user from a flow
/// that was never going to finish.
/// </summary>
public static class FirstRunFlowPoll {
    /// <summary>What to do about one response. <paramref name="statusCode"/> 0 is a transport failure;
    /// <paramref name="bodyRead"/> is false when a 200 arrived with a body this build could not
    /// read, which is a blip rather than an answer.</summary>
    public static FirstRunPollVerdict Classify(int statusCode, bool bodyRead) => statusCode switch {
        200 when bodyRead => FirstRunPollVerdict.State,

        // Deliberately indistinguishable from an unknown id: a 403 would confirm the flow exists, and
        // the id is the only thing an attacker would have had to guess.
        404 => FirstRunPollVerdict.Gone,

        410 => FirstRunPollVerdict.Expired,
        429 => FirstRunPollVerdict.SlowDown,

        // Terminal rather than retried. The bearer was minted before the loop started and nothing in
        // it refreshes one, so every later tick answers the same 401 — seventeen minutes of polling
        // followed by "expired", for a flow that was only ever a re-login away. The budget below is
        // far shorter than any token's life, so meeting this at all means something else changed.
        401 or 403 => FirstRunPollVerdict.Unauthenticated,

        // 408 is the server asking for another go; every other 4xx is this build sending something the
        // server will refuse identically next time.
        408                   => FirstRunPollVerdict.Wait,
        >= 400 and < 500      => FirstRunPollVerdict.Gone,

        // 200 with an unreadable body, 5xx, and 0: the flow is probably fine and the next tick will say so.
        _ => FirstRunPollVerdict.Wait
    };
}
