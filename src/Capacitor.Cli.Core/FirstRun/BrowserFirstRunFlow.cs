using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>Creates the flow, opens the browser on it, and polls it as itself. The create runs before
/// the browser is opened so the flow has an owner from its first request, and the URL is composed
/// here rather than taken from the server — nothing server-supplied reaches the shell-executed open
/// to validate.</summary>
/// <param name="actions">What this host can do to the machine when the browser asks. Null performs
/// nothing, which is the honest state for a host with no such capability — the request stays outstanding
/// and the screen goes on saying it is waiting.</param>
/// <param name="importing">The Import step's scan and run. Null leaves the screen waiting on a report
/// that never comes, so a host that renders the flow at all should supply one.</param>
/// <param name="interrupts">Where to leave the "this machine has gone" callback for an interrupt handler
/// to find. Defaults to the process-global one the CLI's signal handlers read; passing another keeps an
/// ordinary run out of process state.</param>
public sealed class BrowserFirstRunFlow(
        IFirstRunFlowChannel     channel,
        IFirstRunFlowProgress    progress,
        IBrowserLauncher         launcher,
        TimeProvider?            clock     = null,
        IKeyWatcher?             keys      = null,
        IFirstRunMachineActions? actions   = null,
        IFirstRunImportLane?     importing = null,
        IFirstRunInterrupts?     interrupts = null) {
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly IKeyWatcher  _keys  = keys ?? ConsoleKeyWatcher.Instance;

    readonly IFirstRunInterrupts _interrupts = interrupts ?? FirstRunInterruptRelinquish.Process;

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

    /// <summary>Retries the finishing tick gives an outcome that has not been reported, on top of the
    /// attempt it already made. Small: it covers a blip, and holding a finished flow open longer than that
    /// trades the result the user is waiting for against a report they are no longer looking at.</summary>
    const int FlushRetries = 2;

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
        launcher.TryOpen(setupUrl);

        // The poll's own assignment is the single write that publishes the result, so an interrupt sees
        // either a leg still waiting or a settled one, never a half-decided state.
        FirstRunFlowResult? settled = null;

        using var notice = _interrupts.Arm(
            (reason, token) => RelinquishAsync(serverUrl, flowId, reason, token),
            interruptReason: () => InterruptReason(Volatile.Read(ref settled)));

        try {
            Volatile.Write(ref settled, await PollAsync(serverUrl, flowId, report, ct));
        } finally {
            progress.WaitEnded();
        }

        // Claimed once, so whichever path gets there first is the only one that sends and the browser
        // cannot be told two opposite things.
        await notice.SendAsync(ReasonFor(settled!), ct);

        return settled!;
    }

    /// <summary>
    /// What the LEG tells the browser about its own ending, or null when there is nothing to tell it.
    ///
    /// <para><b>A dismissal is a handover and nothing else is.</b> It is the one exit where the terminal
    /// carries on with everything the browser settled, so it is the one where the page must not send anyone
    /// back to <c>kcap setup</c>. A backstop that elapsed and a leg that failed both leave nothing
    /// running.</para>
    ///
    /// <para><b>Finished sends nothing, and that guard is the load-bearing one:</b> the flow is over on its
    /// own terms and the browser is rendering the payoff. Expired needs nothing either — the server already
    /// refuses a flow past its lifetime, and the page says so.</para>
    /// </summary>
    static string? ReasonFor(FirstRunFlowResult settled) => settled switch {
        FirstRunFlowResult.Dismissed                              => FirstRunRelinquishReasons.Handover,
        FirstRunFlowResult.Abandoned or FirstRunFlowResult.Failed  => FirstRunRelinquishReasons.Stopped,
        _                                                         => null
    };

    /// <summary>
    /// What an INTERRUPT tells the browser, given whatever the leg has published so far.
    ///
    /// <para><b>Never the leg's own reason.</b> An interrupt is the process being killed, so nothing is
    /// carrying on however the poll ended — borrowing <see cref="ReasonFor"/> here would tell someone who
    /// had chosen their terminal that it had taken over, as it died, leaving the one tail that states no
    /// remedy at all.</para>
    ///
    /// <para><b>The published result decides only WHETHER there is anything to say.</b> A leg that would
    /// send nothing stays silent under an interrupt too, so a flow that reached its payoff keeps it.</para>
    /// </summary>
    static string? InterruptReason(FirstRunFlowResult? settled) =>
        settled is null || ReasonFor(settled) is not null ? FirstRunRelinquishReasons.Stopped : null;

    /// <summary>Says the machine has gone. Swallows everything: this runs as the leg ends, and a setup that
    /// otherwise worked must not report a failure because one best-effort POST did not land.</summary>
    async Task RelinquishAsync(string serverUrl, string flowId, string reason, CancellationToken ct) {
        try {
            await channel.RelinquishAsync(serverUrl, flowId, reason, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Propagated, as every other await in this class does: a caller's cancel is not this method's
            // to swallow, and the poll would already have thrown on one.
            throw;
        } catch (Exception) {
            // The channel already degrades a transport failure to a status code, so reaching here means
            // something unexpected — and there is still nothing useful to do about it.
        }
    }

    async Task<FirstRunFlowResult> PollAsync(
            string serverUrl, string flowId, FirstRunMachineReport report, CancellationToken ct) {
        var interval = PollInterval;
        var deadline = _clock.GetUtcNow() + PollBudget;
        var first    = true;

        FirstRunFlowResponse? last = null;

        // Keyed on the REQUEST, not the capability, so a second press after an outcome runs again. Two
        // collections because they guard opposite things: an admin prompt must never be raised twice, and
        // a report must keep being retried until it lands.
        var performed = new Dictionary<FirstRunMachineActionRequest, FirstRunMachineActionResult>();
        var reported  = new HashSet<FirstRunMachineActionRequest>();

        var import = new ImportLaneState();

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

                    // Before the finished test, so a request made on the last screen is not abandoned by a
                    // flow that settles in the same tick — and the finished test then runs again below,
                    // because the budget can pass while the user answers an admin prompt.
                    await PerformRequestedAsync(serverUrl, flowId, last!, performed, reported, ct);

                    // Both lanes can outlast a poll interval by minutes, so the budget is measured
                    // against time spent WAITING: a disk scan or an upload is work, not a terminal
                    // nobody is sitting at, and letting it eat the backstop would abandon a flow that
                    // is progressing.
                    deadline += await RunImportLaneAsync(serverUrl, flowId, last!, report, import, ct);
                    deadline += await ActOnImportDecisionAsync(serverUrl, flowId, last!, import, ct);

                    if (FirstRunFlowOutcomes.IsFinished(last!)) {
                        await FlushReportsAsync(serverUrl, flowId, performed, reported, import, ct);

                        return new FirstRunFlowResult.Finished(last!);
                    }

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

    /// <summary>
    /// Scans for importable history once the Agents step has settled, and keeps trying to deliver the
    /// report until the server takes it.
    ///
    /// <para><b>Gated on the Agents step, because its answer is the vendor filter.</b> Scanning first
    /// and subtracting afterwards would report figures for agents the user had just declined, and the
    /// screen's whole job is to state what a selection will import.</para>
    /// </summary>
    /// <returns>How long this took, for the caller to add back to the budget.</returns>
    async Task<TimeSpan> RunImportLaneAsync(
            string                serverUrl,
            string                flowId,
            FirstRunFlowResponse  view,
            FirstRunMachineReport report,
            ImportLaneState       state,
            CancellationToken     ct) {
        if (importing is null || state.Delivered) return TimeSpan.Zero;

        var began = _clock.GetUtcNow();

        if (!state.Scanned && FirstRunFlowOutcomes.IsSettled(view, FirstRunFlowStep.Agents)) {
            state.Scanned = true;

            progress.Discovering();

            var vendors = FirstRunFlowOutcomes.VendorsToImportFrom(report, FirstRunFlowOutcomes.Agents(view));

            // A scan that throws leaves the screen waiting, which is what it should say: nothing was
            // learned about this disk, and claiming an empty one would be a failure reported as a result.
            // Stamped before the scan, and reused when the decision is acted on: the counts the
            // screen shows and the import that follows them have to mean the same window.
            state.DiscoveredAsOf = _clock.GetUtcNow();

            try {
                state.Discovered = await importing.DiscoverAsync(vendors, state.DiscoveredAsOf.Value, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                state.Discovered = null;
            }
        }

        if (state.Discovered is { } found) {
            state.Delivered = (await channel.ReportImportAsync(serverUrl, flowId, found, ct)).Recorded;
        }

        return _clock.GetUtcNow() - began;
    }

    /// <summary>
    /// Runs the decision, once per distinct answer.
    ///
    /// <para><b>Polling stops for the duration.</b> The import writes its own progress, and two live
    /// renderables cannot share a terminal — so this blocks rather than running alongside. Nothing is
    /// lost by it: the only thing left to notice is the flow finishing, and the next tick sees that.</para>
    /// </summary>
    /// <returns>How long this took, for the caller to add back to the budget.</returns>
    async Task<TimeSpan> ActOnImportDecisionAsync(
            string serverUrl, string flowId, FirstRunFlowResponse view, ImportLaneState state,
            CancellationToken ct) {
        if (importing is null) return TimeSpan.Zero;

        // First, so a report that did not land last tick is retried without waiting for the decision to
        // change — which it never will, once it has been acted on.
        //
        // Deliberately uncredited, unlike the import below: this runs on every tick for as long as an
        // outcome is owed, so crediting a stalled POST back to the poll budget would let a server that
        // never accepts the report stretch the flow's own backstop from minutes into hours. A refused
        // report is a blip; only the run is progress.
        await DeliverOutcomeAsync(serverUrl, flowId, state, ct);

        if (!FirstRunFlowOutcomes.IsSettled(view, FirstRunFlowStep.Import)) return TimeSpan.Zero;

        // A decision this build cannot read is REPORTED, not re-read: polling again cannot make a newer
        // server's window or titles vocabulary readable, so the cursor moves and the screen is told why
        // rather than left waiting on a machine that has silently given up.
        if (FirstRunFlowOutcomes.Import(view) is not { } answer) {
            if (view.ImportDecidedAt is { } unreadable && state.ImportedThrough != unreadable) {
                state.ImportedThrough = unreadable;
                state.Outcome        = Refusal(unreadable, FirstRunImportOutcomeReasons.DecisionUnreadable);

                await DeliverOutcomeAsync(serverUrl, flowId, state, ct);
            }

            return TimeSpan.Zero;
        }

        if (state.ImportedThrough == answer.DecidedAt) return TimeSpan.Zero;

        // Stamped before the run, not after: a throw must not leave the same answer to be run again on
        // the next tick, which would repeat an upload rather than retry a failed one.
        state.ImportedThrough = answer.DecidedAt;

        // "Import nothing" is an answer, and there is nothing to run for it — but the screen waits on
        // this machine to say the run is over, so a clean zero is still owed.
        //
        // Only when it really is a decline: an answer left empty because every level in it was
        // unreadable asked for imports and got none, and reporting that as a clean zero states the one
        // thing the screen must not — "you chose not to" — about a user who chose otherwise.
        if (answer.Choices.Count == 0) {
            state.Outcome = answer.IsDecline
                ? Outcome(answer.DecidedAt, default, null)
                : Refusal(answer.DecidedAt, FirstRunImportOutcomeReasons.DecisionUnreadable);

            await DeliverOutcomeAsync(serverUrl, flowId, state, ct);

            return TimeSpan.Zero;
        }

        // Nothing to scan, so nothing to run. Reported as a refusal rather than as three zeroes, which
        // is what a clean import over an already-loaded history also looks like.
        if (answer.NoReadableVendors) {
            state.Outcome = Refusal(answer.DecidedAt, FirstRunImportOutcomeReasons.NoReadableAgents);

            await DeliverOutcomeAsync(serverUrl, flowId, state, ct);

            return TimeSpan.Zero;
        }

        var began = _clock.GetUtcNow();

        progress.Importing(answer.Choices.Count, SessionsInScope(state.Discovered, answer));

        FirstRunImportTotals? moved = null;

        try {
            moved = await importing.ImportAsync(answer, state.WindowsAsOf(_clock), ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            // Best-effort, as setup's own import step is: the flow is not the place a failed backfill
            // ends a run, and `kcap import` remains the way to retry it.
        } finally {
            progress.ImportEnded();
        }

        // Nothing is sent for a run that lost a pass. Its sessions are unaccounted, and three counts
        // cannot say so — the surviving pass's figures alone would report a clean import.
        if (moved is { } totals) {
            state.Outcome = Outcome(answer.DecidedAt, totals, null);

            await DeliverOutcomeAsync(serverUrl, flowId, state, ct);
        }

        return _clock.GetUtcNow() - began;
    }

    /// <summary>The outcome as the route takes it.</summary>
    static ReportFirstRunImportOutcomeRequest Outcome(
            DateTimeOffset decidedAt, FirstRunImportTotals totals, string? reason) =>
        new() {
            DecidedAt = decidedAt,
            Imported  = totals.Imported,
            Skipped   = totals.Skipped,
            Failed    = totals.Failed,
            Reason    = reason
        };

    /// <summary>A refusal: three zeroes and a token. The server rejects the report outright if a reason
    /// arrives on counts that moved something, so the zeroes are part of the contract rather than a
    /// convenience.</summary>
    static ReportFirstRunImportOutcomeRequest Refusal(DateTimeOffset decidedAt, string reason) =>
        Outcome(decidedAt, default, reason);

    /// <summary>Hands over the owed outcome, keeping it for a later tick unless the server took it.</summary>
    async Task DeliverOutcomeAsync(
            string serverUrl, string flowId, ImportLaneState state, CancellationToken ct) {
        if (state.Outcome is not { } owed) return;

        if ((await channel.ReportImportOutcomeAsync(serverUrl, flowId, owed, ct)).Recorded)
            state.Outcome = null;
    }

    /// <summary>The import lane's state across polls, in one object because an async method cannot
    /// take it by reference.</summary>
    sealed class ImportLaneState {
        /// <summary>What the scan produced, held until the server takes it.</summary>
        public ReportFirstRunImportRequest? Discovered { get; set; }

        /// <summary>The scan has been attempted. It runs once: a browser tab does not outlive the
        /// disk changing under it by enough to matter, and rescanning would cost minutes per tick.</summary>
        public bool Scanned { get; set; }

        /// <summary>The report reached the server. Until it does, every tick tries again.</summary>
        public bool Delivered { get; set; }

        /// <summary>The outcome still owed to the server, or null when there is none left to send.
        /// Retried every tick like the discovery report, and cleared only once taken.</summary>
        public ReportFirstRunImportOutcomeRequest? Outcome { get; set; }

        /// <summary>The answer already run, as a cursor rather than a flag. The server advances the
        /// stamp only when the answer CHANGES, so going Back and widening the window runs the wider
        /// import, while re-confirming the same answer runs nothing.</summary>
        public DateTimeOffset? ImportedThrough { get; set; }

        /// <summary>When the scan ran, which is the instant its window counts were built against.</summary>
        public DateTimeOffset? DiscoveredAsOf { get; set; }

        /// <summary>The date the import's <c>--since</c> resolves against: the scan's, so the figure
        /// the screen showed and the history the import selects agree. Falls back to now only when
        /// there was no scan to agree with.</summary>
        public DateOnly WindowsAsOf(TimeProvider clock) =>
            DateOnly.FromDateTime((DiscoveredAsOf ?? clock.GetUtcNow()).UtcDateTime);
    }

    /// <summary>Sessions the chosen window holds across the chosen repositories, or null when any of
    /// them reported no count for it — a total that quietly omitted a repository would be the wrong
    /// number stated confidently.</summary>
    static int? SessionsInScope(ReportFirstRunImportRequest? report, FirstRunImportAnswer answer) {
        if (report is null) return null;

        var chosen = answer.Choices.Select(c => c.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total  = 0;

        foreach (var repo in report.Repos) {
            if (!chosen.Contains($"{repo.Owner}/{repo.Name}")) continue;
            if (!repo.Sessions.TryGetValue(answer.Window, out var found)) return null;

            total += found;
        }

        return total;
    }

    /// <summary>Performs the actions the browser is asking for, and reports each one back. <b>Nothing here
    /// composes a command from what the server said</b> — a capability token crossed, and the host resolves
    /// the operation behind it.</summary>
    async Task PerformRequestedAsync(
            string                                                               serverUrl,
            string                                                               flowId,
            FirstRunFlowResponse                                                 view,
            Dictionary<FirstRunMachineActionRequest, FirstRunMachineActionResult> performed,
            HashSet<FirstRunMachineActionRequest>                                reported,
            CancellationToken                                                    ct) {
        if (actions is null) return;

        foreach (var request in FirstRunFlowOutcomes.MachineActions(view)) {
            // An unadvertised capability is left outstanding rather than reported as failed: "this machine
            // cannot do that" and "it was tried and did not work" are different facts.
            if (!actions.Capabilities.Contains(request.Capability, StringComparer.Ordinal)) continue;

            if (!performed.TryGetValue(request, out var result)) {
                // Said before the attempt, not after: the shim prompts for an admin password, and a
                // password dialog nobody was warned about is indistinguishable from malware.
                progress.PerformingAction(request.Capability);

                try {
                    result = await actions.PerformAsync(request.Capability, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    // Propagated, not swallowed: every other await in this loop lets the caller's cancel
                    // out, and catching it here would let a cancelled leg go on to report itself finished.
                    throw;
                } catch (Exception) {
                    // `failed` rather than a refusal: something was attempted. A screen left waiting on an
                    // outcome that threw is the state this lane exists to avoid. An internal cancellation
                    // that is not the caller's lands here too, which is what it is.
                    result = new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Failed, null);
                }

                // Recorded before the report, so a POST that fails cannot re-raise the prompt.
                performed[request] = result;
            }

            if (reported.Contains(request)) continue;

            await ReportAsync(serverUrl, flowId, request, result, reported, ct);
        }
    }

    /// <summary>Reports one outcome, recording it as reported only when the server took it. A refusal
    /// leaves the request outstanding, which is what the next tick retries against.</summary>
    async Task ReportAsync(
            string serverUrl, string flowId, FirstRunMachineActionRequest request,
            FirstRunMachineActionResult result, HashSet<FirstRunMachineActionRequest> reported,
            CancellationToken ct) {
        var outcome = await channel.ReportMachineActionAsync(
            serverUrl, flowId,
            new ReportFirstRunMachineActionRequest {
                Capability  = request.Capability,
                RequestedAt = request.RequestedAt,
                Outcome     = result.Outcome,
                Reason      = result.Reason
            },
            ct);

        if (outcome.Recorded) reported.Add(request);
    }

    /// <summary>
    /// A last bounded attempt at any outcome that has not been reported yet, for the tick that ends the
    /// loop.
    ///
    /// <para><b>The per-tick retry needs a next tick, and the finishing one has none.</b> A user who
    /// presses the fix and then clicks through to the end can otherwise lose the outcome to a single blip,
    /// on the one path where nothing comes back to try again.</para>
    ///
    /// <para><b>Bounded, because a finished flow must not be held open for this.</b> What survives a
    /// sustained failure here is the request staying outstanding, which is the honest reading anyway — the
    /// browser goes on saying it asked. Interrupted exits do not flush at all, for the same reason: an
    /// abandoned or dismissed leg has not finished doing anything.</para>
    /// </summary>
    async Task FlushReportsAsync(
            string serverUrl, string flowId,
            Dictionary<FirstRunMachineActionRequest, FirstRunMachineActionResult> performed,
            HashSet<FirstRunMachineActionRequest> reported, ImportLaneState import, CancellationToken ct) {
        for (var retry = 0; retry < FlushRetries; retry++) {
            var outstanding = performed.Where(p => !reported.Contains(p.Key)).ToList();

            // The import outcome flushes here too: the poll returns on a finished flow, so this is the
            // last tick that exists and an outcome left owed would never be sent at all.
            if (outstanding.Count == 0 && import.Outcome is null) return;

            // Always gapped, including the first: this runs immediately after the tick's own attempt
            // failed, and an instant re-POST at the same server answers the same way.
            await Task.Delay(PollInterval, _clock, ct);

            foreach (var (request, result) in outstanding)
                await ReportAsync(serverUrl, flowId, request, result, reported, ct);

            await DeliverOutcomeAsync(serverUrl, flowId, import, ct);
        }
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
