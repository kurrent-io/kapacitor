using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>Creates the flow, opens the browser on it, and polls it as itself. The create runs before
/// the browser is opened so the flow has an owner from its first request, and the URL is composed
/// here rather than taken from the server — nothing server-supplied reaches the shell-executed open
/// to validate.</summary>
public sealed class BrowserFirstRunFlow(
        IFirstRunFlowChannel     channel,
        IFirstRunFlowProgress    progress,
        TimeProvider?            clock       = null,
        Func<string, bool>?      openBrowser = null,
        IKeyWatcher?             keys        = null) {
    readonly TimeProvider       _clock       = clock ?? TimeProvider.System;
    readonly Func<string, bool> _openBrowser = openBrowser ?? SystemBrowser.TryOpen;
    readonly IKeyWatcher        _keys        = keys ?? ConsoleKeyWatcher.Instance;

    /// <summary>
    /// The backstop, not the way out. Nothing like the flow's own 12-hour TTL, which is sized for a
    /// link surviving a working day rather than for a terminal sitting open on one — but half an hour
    /// of dots is still no answer to a closed tab, which is why a keypress ends the wait and this only
    /// catches the terminal nobody is sitting at.
    /// </summary>
    static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(30);

    /// <summary>Tight, because a human is clicking and the payoff is the terminal reacting as they do.
    /// The server has no floor on this route.</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>The delay is slept in slices this long so a keypress is noticed within one slice
    /// rather than after the whole interval — a 30s backoff must not swallow the escape hatch.</summary>
    static readonly TimeSpan KeyPollSlice = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How many fresh ids to try against a 409. A 409 means the id belongs to someone else, which
    /// takes a 128-bit collision or a colleague who guessed — so one retry already covers everything
    /// that is not a broken generator, and three is only generous. It is NOT a credentials problem,
    /// which is why the server chose that status: retrying with a new id is the whole remedy.
    /// </summary>
    const int CreateAttempts = 3;

    /// <summary>Runs the leg. Never throws for a reachable failure — every way this ends is a
    /// <see cref="FirstRunFlowResult"/>, because setup carries on either way.</summary>
    public async Task<FirstRunFlowResult> RunAsync(
            string serverUrl, FirstRunMachineReport report, CancellationToken ct) {
        string flowId;
        var    attempt = 0;

        while (true) {
            flowId = FirstRunFlowId.New();

            var created = await channel.CreateAsync(serverUrl, flowId, report, ct);

            // Every one of these means "no flow here", and every one has the same remedy: carry on
            // with the setup that already works. The routes are mapped only when the tenant has
            // Features:FirstRun on, so a 404 is the availability oracle — a fact to observe rather
            // than a server version to guess at.
            //
            // 401/403 are in the set even though this route IS authenticated, and that is a trade
            // rather than an oversight: a gateway answering them on a path it does not know is
            // indistinguishable from the feature being off, and a login succeeded seconds ago, so the
            // odds favour the route. Guessing wrong here skips an additive leg silently; guessing the
            // other way prints an alarming auth failure on every tenant that simply has the flow off.
            if (created.StatusCode is 404 or 401 or 403 or 405) return new FirstRunFlowResult.Unavailable();

            if (created.StatusCode == 429)
                return new FirstRunFlowResult.RateLimited(created.RetryAfter ?? TimeSpan.FromMinutes(10));

            if (created.StatusCode == 409) {
                if (++attempt >= CreateAttempts)
                    return new FirstRunFlowResult.Failed("Could not claim a setup link on this server.");

                continue;
            }

            if (created.StatusCode == 0)
                return new FirstRunFlowResult.Failed("Could not reach the server to start browser setup.");

            if (created.StatusCode is < 200 or >= 300)
                return new FirstRunFlowResult.Failed(
                    $"The server did not accept a browser setup link (HTTP {created.StatusCode}).");

            // The poll side treats this exact condition as a blip rather than an answer, and so does
            // the create: the server answered, and the reply was not readable by this build.
            if (created.Body is null)
                return new FirstRunFlowResult.Failed("The server answered, but its reply could not be read.");

            // A flow other than the one asked for is not an answer to the question. It cannot happen
            // against the server this was written for, which is exactly why a disagreement is worth
            // stopping on rather than polling an id this process never generated.
            if (!string.Equals(created.Body.FlowId, flowId, StringComparison.Ordinal))
                return new FirstRunFlowResult.Failed("The server answered about a different setup link.");

            break;
        }

        var setupUrl = $"{serverUrl.TrimEnd('/')}/setup?s={Uri.EscapeDataString(flowId)}";

        // Drained before the prompt renders, so the two presses stay apart: one that preceded this
        // leg — the Return that confirmed an earlier step — is not an answer to "press any key to
        // carry on here", and one made in response to that prompt is a real dismissal, never stale.
        if (_keys.CanWatch && _keys.KeyAvailable) _keys.Drain();

        progress.Opening(setupUrl);
        _openBrowser(setupUrl);

        try {
            return await PollAsync(serverUrl, flowId, ct);
        } finally {
            progress.WaitEnded();
        }
    }

    async Task<FirstRunFlowResult> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
        var interval = PollInterval;
        var deadline = _clock.GetUtcNow() + PollBudget;
        var first    = true;

        FirstRunFlowResponse? last = null;

        while (_clock.GetUtcNow() < deadline) {
            // Polled before the first sleep: a flow the browser has already finished — a resumed link,
            // or a tab that was quicker than this process — should not wait out an interval to be noticed.
            if (!first) {
                if (await WaitForIntervalAsync(interval, deadline, last, ct)) return new FirstRunFlowResult.Dismissed(last);

                // The sleep crossed the budget's deadline — the backstop has been reached, and polling
                // once more would extend it by the backoff plus the HTTP timeout.
                if (_clock.GetUtcNow() >= deadline) break;
            }

            first = false;

            var poll = await channel.PollAsync(serverUrl, flowId, ct);

            // A key that arrived while the poll was in flight: noticed here rather than after another
            // interval. Drained rather than read, because the key is usually followed by a Return that
            // the next prompt would otherwise take as its answer.
            if (DismissIfKeyDown(last) is { } duringPoll) return duringPoll;

            switch (FirstRunFlowPoll.Classify(poll.StatusCode, poll.Body is not null)) {
                case FirstRunPollVerdict.State:
                    // The create path's guard, applied to the poll too: an answer about a flow other
                    // than the one asked for is not an answer, and a disagreement is worth stopping on
                    // rather than reporting another flow's outcome as this one's.
                    if (!string.Equals(poll.Body!.FlowId, flowId, StringComparison.Ordinal))
                        return new FirstRunFlowResult.Failed("The server answered about a different setup link.");

                    last = poll.Body;

                    // Healthy again — back to the tight cadence, so a terminal reacts to the human
                    // at the speed the human works at.
                    interval = PollInterval;

                    if (FirstRunFlowOutcomes.IsFinished(last!)) return new FirstRunFlowResult.Finished(last!);

                    progress.PollTick();

                    break;

                case FirstRunPollVerdict.Expired:
                    return new FirstRunFlowResult.Expired();

                case FirstRunPollVerdict.Gone:
                    return new FirstRunFlowResult.Failed("The server no longer recognises this setup link.");

                case FirstRunPollVerdict.Unauthenticated:
                    return new FirstRunFlowResult.Failed(
                        "The server stopped accepting this sign-in mid-setup. Run 'kcap login' to re-authenticate.");

                case FirstRunPollVerdict.SlowDown:
                    // The server's own Retry-After wins when it is longer than the current gap; every
                    // other unhappy poll grows the gap too, so a down or rate-limiting server is not
                    // hammered at the base cadence. Only a good state restores it.
                    interval = Backoff(interval, poll.RetryAfter);
                    progress.PollTick();

                    break;

                default:
                    interval = Backoff(interval, null);
                    progress.PollTick();

                    break;
            }
        }

        return new FirstRunFlowResult.Abandoned(last);
    }

    /// <summary>Dismisses when a key is down, draining it. Null when there is nothing to dismiss, so
    /// the call site can read it as "did a key arrive while I awaited?".</summary>
    FirstRunFlowResult? DismissIfKeyDown(FirstRunFlowResponse? last) {
        if (!_keys.CanWatch || !_keys.KeyAvailable) return null;

        _keys.Drain();

        return new FirstRunFlowResult.Dismissed(last);
    }

    /// <summary>Sleeps out <paramref name="interval"/> in <see cref="KeyPollSlice"/> slices, returning
    /// true when a keypress ended the wait early (it has been drained). Capped at what remains of the
    /// budget: a server Retry-After longer than the backstop must not extend the flow past its deadline.</summary>
    async Task<bool> WaitForIntervalAsync(TimeSpan interval, DateTimeOffset deadline, FirstRunFlowResponse? last, CancellationToken ct) {
        var remaining = deadline - _clock.GetUtcNow();

        if (remaining < interval) interval = remaining;

        var waited = TimeSpan.Zero;

        while (waited < interval) {
            if (DismissIfKeyDown(last) is not null) return true;

            var slice = interval - waited < KeyPollSlice ? interval - waited : KeyPollSlice;
            await Task.Delay(slice, _clock, ct);
            waited += slice;
        }

        return false;
    }

    /// <summary>The next poll gap. The server's Retry-After is its rate-limit window and is honoured
    /// as-is (never below the base cadence); only the locally computed doubling is capped at
    /// <see cref="MaxInterval"/>, so a down or rate-limiting server is not hammered.</summary>
    static TimeSpan Backoff(TimeSpan current, TimeSpan? retryAfter) {
        if (retryAfter is { } ra) return ra > PollInterval ? ra : PollInterval;

        var doubled = current * 2;

        return doubled > MaxInterval ? MaxInterval : doubled;
    }
}
