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

    /// <summary>Applied on a 429 against the poll.</summary>
    static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(2);

    static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many fresh ids to try against a 409. A 409 means the id belongs to someone else, which
    /// takes a 128-bit collision or a colleague who guessed — so one retry already covers everything
    /// that is not a broken generator, and three is only generous. It is NOT a credentials problem,
    /// which is why the server chose that status: retrying with a new id is the whole remedy.
    /// </summary>
    const int CreateAttempts = 3;

    /// <summary>Runs the leg. Never throws for a reachable failure — every way this ends is a
    /// <see cref="FirstRunFlowResult"/>, because setup carries on either way.</summary>
    public async Task<FirstRunFlowResult> RunAsync(string serverUrl, string? machine, CancellationToken ct) {
        string flowId;
        var    attempt = 0;

        while (true) {
            flowId = FirstRunFlowId.New();

            var created = await channel.CreateAsync(serverUrl, flowId, machine, ct);

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

            if (created.StatusCode is < 200 or >= 300 || created.Body is null)
                return new FirstRunFlowResult.Failed(
                    $"The server did not accept a browser setup link (HTTP {created.StatusCode}).");

            // A flow other than the one asked for is not an answer to the question. It cannot happen
            // against the server this was written for, which is exactly why a disagreement is worth
            // stopping on rather than polling an id this process never generated.
            if (!string.Equals(created.Body.FlowId, flowId, StringComparison.Ordinal))
                return new FirstRunFlowResult.Failed("The server answered about a different setup link.");

            break;
        }

        var setupUrl = $"{serverUrl.TrimEnd('/')}/setup?s={Uri.EscapeDataString(flowId)}";

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
            if (!first) await Task.Delay(interval, _clock, ct);

            first = false;

            // The way out of a wait whose browser is never coming back — a closed tab, a link opened on
            // a machine that then went away. Drained rather than read, because the key is usually
            // followed by a Return that the next prompt would otherwise take as its answer.
            if (_keys.CanWatch && _keys.KeyAvailable) {
                _keys.Drain();

                return new FirstRunFlowResult.Dismissed(last);
            }

            var poll = await channel.PollAsync(serverUrl, flowId, ct);

            switch (FirstRunFlowPoll.Classify(poll.StatusCode, poll.Body is not null)) {
                case FirstRunPollVerdict.State:
                    last = poll.Body;

                    if (FirstRunFlowOutcomes.IsFinished(last!)) return new FirstRunFlowResult.Finished(last!);

                    progress.PollTick();

                    break;

                case FirstRunPollVerdict.Expired:
                    return new FirstRunFlowResult.Expired();

                case FirstRunPollVerdict.Gone:
                    return new FirstRunFlowResult.Failed("The server no longer recognises this setup link.");

                case FirstRunPollVerdict.Unauthenticated:
                    return new FirstRunFlowResult.Failed("The server stopped accepting this sign-in mid-setup.");

                case FirstRunPollVerdict.SlowDown:
                    interval = interval + SlowDownStep < MaxInterval ? interval + SlowDownStep : MaxInterval;
                    progress.PollTick();

                    break;

                default:
                    progress.PollTick();

                    break;
            }
        }

        return new FirstRunFlowResult.Abandoned(last);
    }
}
