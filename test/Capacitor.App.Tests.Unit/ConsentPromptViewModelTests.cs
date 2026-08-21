using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.ConsentEntries;

namespace Capacitor.App.Tests.Unit;

/// The full prompt matrix (spec §6/§10). Everything runs on the headless session's REAL dispatcher
/// scheduler — that is what production's ObserveOn(RxSchedulers.MainThreadScheduler) actually runs
/// under, and the resolve continuation's ordering against the cache eviction only exists there.
/// Time is a FakeTimeProvider and the heartbeat a FakeTicker: no test sleeps, and the 2-second
/// terminal hold is counted in ticks.
public class ConsentPromptViewModelTests {
    // ---- 1: queue order + position indicator ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Current_is_oldest_by_requested_at_then_id_and_position_text_reads_1_of_n() {
        var (currentId, positionText, positionVisible, soloVisible) = await AvaloniaSession.DispatchAsync(() => {
            using var h = new PromptHarness(
                Entry("c", "pc", requestedAt: T0.AddSeconds(10)),
                Entry("b", "pb"),
                Entry("a", "pa")); // ties with "b" on RequestedAt: ordinal tiebreak wins
            h.Activate();

            using var solo = new PromptHarness(Entry("only", "ponly"));
            solo.Activate();

            return (h.Vm.Current!.RequestId, h.Vm.PositionText, h.Vm.PositionVisible, solo.Vm.PositionVisible);
        });

        await Assert.That(currentId).IsEqualTo("a");
        await Assert.That(positionText).IsEqualTo("1 of 3");
        await Assert.That(positionVisible).IsTrue();
        await Assert.That(soloVisible).IsFalse(); // "1 of 1" is noise
    }

    // ---- 2: the pin ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pin_survives_cache_changes_while_resolving_or_in_terminal_hold() {
        var (whileResolving, whileHolding, afterHold) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            var gate = h.Consent.Arm();
            var exec = h.Vm.DenyCommand.Execute().ToTask();
            PromptHarness.Pump();

            h.Add(Entry("older", "p-older", requestedAt: T0.AddSeconds(-60))); // sorts ahead of the pin
            var resolving = h.Vm.Current!.RequestId;

            gate.SetResult(new ConsentResolveOutcome(ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.NotRequested, null));
            await exec;
            PromptHarness.Pump();

            h.Add(Entry("older2", "p-older2", requestedAt: T0.AddSeconds(-120)));
            var holding = h.Vm.Current!.RequestId;

            h.Tick();
            h.Tick();
            return (resolving, holding, h.Vm.Current!.RequestId);
        });

        await Assert.That(whileResolving).IsEqualTo("a1");
        await Assert.That(whileHolding).IsEqualTo("a1");
        await Assert.That(afterHold).IsEqualTo("older2"); // the advance releases the pin to the head
    }

    // ---- 3: content projection ----

    [Test]
    [Arguments("agent", "Agent")]
    [Arguments("review", "Review")]
    [Arguments("review-flow", "Review flow")]
    [Arguments("something-new", "something-new")] // unrecognized renders verbatim, never a wrong label
    public async Task Kind_labels(string kind, string expected) {
        await Assert.That(ConsentPromptViewModel.KindLabelOf(kind)).IsEqualTo(expected);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Requester_falls_back_display_then_id_then_unknown() {
        var (display, id, unknown, kindLabel, repoLeaf, repoFull, vendor) = await AvaloniaSession.DispatchAsync(() => {
            static string Requester(string? requesterDisplay, string? requester) {
                using var h = new PromptHarness(Entry(requesterDisplay: requesterDisplay, requester: requester));
                h.Activate();
                return h.Vm.RequesterText;
            }

            using var main = new PromptHarness(Entry(
                kind: "review-flow", repoPath: "/repos/kcap-cli/.claude/worktrees/tender-honking-pebble", vendor: "codex"));
            main.Activate();

            return (Requester("Alice", "github:1"), Requester(null, "github:1"), Requester(null, null),
                    main.Vm.KindLabel, main.Vm.RepoLeaf, main.Vm.RepoFull, main.Vm.VendorText);
        });

        await Assert.That(display).IsEqualTo("Alice");
        await Assert.That(id).IsEqualTo("github:1");
        await Assert.That(unknown).IsEqualTo("unknown");
        await Assert.That(kindLabel).IsEqualTo("Review flow");
        await Assert.That(repoLeaf).IsEqualTo("kcap-cli"); // the worktree leaf is noise (RepoLabel)
        await Assert.That(repoFull).IsEqualTo("/repos/kcap-cli/.claude/worktrees/tender-honking-pebble");
        await Assert.That(vendor).IsEqualTo("codex");
    }

    // ---- 4: countdown, and expiry as a non-verdict ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Countdown_ticks_and_expiry_is_not_a_verdict() {
        var (initial, ticked, expired, enabledWhileExpired, phase) = await AvaloniaSession.DispatchAsync(() => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            var start = h.Vm.CountdownText;
            h.Tick(TimeSpan.FromSeconds(10));
            var mid = h.Vm.CountdownText;
            h.Tick(TimeSpan.FromSeconds(25)); // past DeadlineHint (T0 + 30s)

            return (start, mid, h.Vm.CountdownText, h.Vm.ButtonsEnabled, h.Vm.Phase);
        });

        await Assert.That(initial).IsEqualTo("Expires in 30s");
        await Assert.That(ticked).IsEqualTo("Expires in 20s");
        await Assert.That(expired).IsEqualTo("Response time elapsed — unanswered requests are denied by the daemon");
        await Assert.That(enabledWhileExpired).IsTrue(); // expiry is never a verdict (spec §6)
        await Assert.That(phase).IsEqualTo(ConsentPromptPhase.Expired);
    }

    /// The wall-clock-step case: the hint fired early, the request was still live daemon-side.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Click_after_hint_zero_applies_when_the_daemon_still_had_the_request() {
        var (resolved, currentAfter, phaseText) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"), Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            h.Activate();
            h.Tick(TimeSpan.FromSeconds(40));

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested);
            await h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Consent.Resolved.ToArray(), h.Vm.Current!.RequestId, h.Vm.PhaseText);
        });

        await Assert.That(resolved).IsEquivalentTo([("p1", true, false)], CollectionOrdering.Matching);
        await Assert.That(currentAfter).IsEqualTo("a2"); // applied and advanced, not "expired"
        await Assert.That(phaseText).IsNull();
    }

    /// The other half: the daemon really did time out, so the honest already-decided path runs.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Click_after_hint_zero_runs_already_decided_when_the_daemon_timed_out() {
        var (phaseText, buttonsVisible) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();
            h.Tick(TimeSpan.FromSeconds(40));

            h.Consent.Queue(ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.NotRequested);
            await h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Vm.PhaseText, h.Vm.ButtonsVisible);
        });

        await Assert.That(phaseText).IsEqualTo("Already decided");
        await Assert.That(buttonsVisible).IsFalse();
    }

    // ---- 5: the three buttons ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Buttons_send_the_pinned_targets_prompt_id() {
        var resolved = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(
                Entry("a1", "p1"),
                Entry("a2", "p2", requestedAt: T0.AddSeconds(5)),
                Entry("a3", "p3", requestedAt: T0.AddSeconds(10)));
            h.Activate();

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested);
            await h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.Saved);
            await h.Vm.AllowRememberCommand.Execute().ToTask();
            PromptHarness.Pump();

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested);
            await h.Vm.DenyCommand.Execute().ToTask();
            PromptHarness.Pump();

            return h.Consent.Resolved.ToArray();
        });

        await Assert.That(resolved).IsEquivalentTo(
            [("p1", true, false), ("p2", true, true), ("p3", false, false)], CollectionOrdering.Matching);
    }

    // ---- 6: the save-rule button predicate ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Allow_remember_hidden_for_null_and_empty_requester() {
        var (nullRequester, emptyRequester, named) = await AvaloniaSession.DispatchAsync(() => {
            static bool Visible(string? requester) {
                using var h = new PromptHarness(Entry(requester: requester, requesterDisplay: "Alice"));
                h.Activate();
                return h.Vm.AllowRememberVisible;
            }

            return (Visible(null), Visible(""), Visible("github:1"));
        });

        // A display name is not an identity: only Requester can key a rule (spec §6).
        await Assert.That(nullRequester).IsFalse();
        await Assert.That(emptyRequester).IsFalse();
        await Assert.That(named).IsTrue();
    }

    // ---- 7/8: already decided ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Already_decided_holds_2_ticks_then_advances() {
        var (phaseText, buttonsVisible, afterOneTick, afterTwoTicks) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"), Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            h.Activate();

            h.Consent.Queue(ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.NotRequested);
            await h.Vm.DenyCommand.Execute().ToTask();
            PromptHarness.Pump();

            var text = h.Vm.PhaseText;
            var visible = h.Vm.ButtonsVisible;

            h.Tick();
            var one = h.Vm.Current!.RequestId;
            h.Tick();
            return (text, visible, one, h.Vm.Current!.RequestId);
        });

        await Assert.That(phaseText).IsEqualTo("Already decided");
        await Assert.That(buttonsVisible).IsFalse();
        await Assert.That(afterOneTick).IsEqualTo("a1"); // still held
        await Assert.That(afterTwoTicks).IsEqualTo("a2");
    }

    [Test]
    [Arguments(ConsentRuleOutcome.Saved, "Already decided — your allow rule for Alice was still saved.")]
    [Arguments(ConsentRuleOutcome.Rejected, "Already decided — no rule was saved.")]
    [Arguments(ConsentRuleOutcome.Unknown, "Already decided — this daemon version doesn't report whether your allow rule was saved.")]
    [NotInParallel("AvaloniaSession")]
    public async Task Already_decided_discloses_rule_outcome_after_allow_remember(ConsentRuleOutcome rule, string expected) {
        var (phaseText, notified) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            h.Consent.Queue(ConsentResolveKind.AlreadyDecided, rule);
            await h.Vm.AllowRememberCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Vm.PhaseText, h.Notifier.Notified.ToArray());
        });

        await Assert.That(phaseText).IsEqualTo(expected);
        await Assert.That(notified).IsEmpty(); // the disclosure is the window's, not a toast
    }

    // ---- 9: applied + rule warnings ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Applied_advances_immediately_without_a_toast() {
        var (notified, current, closed) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.Saved);
            await h.Vm.AllowRememberCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Notifier.Notified.ToArray(), h.Vm.Current, h.CloseRequests);
        });

        await Assert.That(notified).IsEmpty();
        await Assert.That(current).IsNull();
        await Assert.That(closed).IsEqualTo(1); // the queue emptied: the window closes itself
    }

    [Test]
    [Arguments(ConsentResolveKind.AppliedRuleRejected, ConsentRuleOutcome.Rejected, "store full", "Decision applied — rule not saved: store full")]
    [Arguments(ConsentResolveKind.RuleSkippedNoRequester, ConsentRuleOutcome.SkippedNoRequester, null, "Decision applied — rule not saved: the request had no requester identity")]
    [Arguments(ConsentResolveKind.AppliedRuleRejected, ConsentRuleOutcome.Unknown, null, "Decision applied — this daemon version doesn't report whether the rule was saved")]
    [NotInParallel("AvaloniaSession")]
    public async Task Rule_warnings_toast_and_still_advance(
            ConsentResolveKind kind, ConsentRuleOutcome rule, string? error, string expected) {
        var (notified, current) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"), Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            h.Activate();

            h.Consent.Queue(kind, rule, error);
            await h.Vm.AllowRememberCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Notifier.Notified.ToArray(), h.Vm.Current!.RequestId);
        });

        await Assert.That(notified).IsEquivalentTo([expected], CollectionOrdering.Matching);
        await Assert.That(current).IsEqualTo("a2");
    }

    /// The eviction and the ack's continuation are two independently posted jobs, so the advance
    /// must not depend on the queue view having caught up: the request it just concluded is never
    /// re-pinned, and a late eviction changes nothing afterwards.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Advance_never_repins_the_request_it_just_concluded() {
        var (afterAck, queuedAtAck, afterEviction) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"), Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            h.Activate();
            h.Consent.ConcludeLate = true;

            h.Consent.Queue(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested);
            await h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            var pinned = h.Vm.Current!.RequestId;
            var queued = h.Vm.Queue.Count; // the concluded entry is still in view

            h.Consent.FlushConclusions();
            PromptHarness.Pump();
            return (pinned, queued, h.Vm.Current!.RequestId);
        });

        await Assert.That(afterAck).IsEqualTo("a2");
        await Assert.That(queuedAtAck).IsEqualTo(2);
        await Assert.That(afterEviction).IsEqualTo("a2");
    }

    /// The ViewModel half of the same defect: a warning that has nowhere to advance to must hold
    /// the pin (and the window) rather than close on the beat it was raised. The multi-entry test
    /// above pins the other half — a queue with somewhere to go still advances immediately.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rule_warning_holds_the_window_when_the_queue_would_empty() {
        var (phaseText, pinned, buttonsVisible, closedDuringHold, afterHold, closedAfterHold) =
            await AvaloniaSession.DispatchAsync(async () => {
                using var h = new PromptHarness(Entry("a1", "p1"));
                h.Activate();

                h.Consent.Queue(ConsentResolveKind.AppliedRuleRejected, ConsentRuleOutcome.Rejected, "store full");
                await h.Vm.AllowRememberCommand.Execute().ToTask();
                PromptHarness.Pump();

                var held = (h.Vm.PhaseText, h.Vm.Current is not null, h.Vm.ButtonsVisible, h.CloseRequests);

                h.Tick();
                h.Tick();
                return (held.PhaseText, held.Item2, held.ButtonsVisible, held.CloseRequests, h.Vm.Current, h.CloseRequests);
            });

        await Assert.That(phaseText).IsEqualTo("Decision applied — rule not saved: store full");
        await Assert.That(pinned).IsTrue();
        await Assert.That(buttonsVisible).IsFalse(); // the request is settled: nothing left to click
        await Assert.That(closedDuringHold).IsEqualTo(0);
        await Assert.That(afterHold).IsNull();
        await Assert.That(closedAfterHold).IsEqualTo(1);
    }

    // ---- 10: transport failure ----

    /// The outcome's rule value on this path describes a rule that was never sent, so it must NOT
    /// be rendered: an Unknown here would otherwise produce the down-level-daemon copy.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Transport_failure_reenables_buttons_and_keeps_current() {
        var (notified, current, enabled, phaseText, queued) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            h.Consent.Queue(ConsentResolveKind.TransportFailure, ConsentRuleOutcome.Unknown, "daemon_unreachable");
            await h.Vm.AllowRememberCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Notifier.Notified.ToArray(), h.Vm.Current!.RequestId, h.Vm.ButtonsEnabled, h.Vm.PhaseText, h.Vm.Queue.Count);
        });

        await Assert.That(notified).IsEquivalentTo(["Daemon unreachable — the request is still pending"], CollectionOrdering.Matching);
        await Assert.That(current).IsEqualTo("a1");
        await Assert.That(enabled).IsTrue();
        await Assert.That(phaseText).IsNull();
        await Assert.That(queued).IsEqualTo(1);
    }

    // ---- 11: expiry never preempts an in-flight resolve ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Expiry_never_preempts_inflight_resolve() {
        var (countdown, pinned, phase, afterAck) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"), Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));
            h.Activate();

            var gate = h.Consent.Arm();
            var exec = h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            h.Tick(TimeSpan.FromSeconds(40)); // past both hints, mid-call
            var text = h.Vm.CountdownText;
            var stillPinned = h.Vm.Current!.RequestId;
            var duringPhase = h.Vm.Phase;

            gate.SetResult(new ConsentResolveOutcome(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested, null));
            await exec;
            PromptHarness.Pump();

            return (text, stillPinned, duringPhase, h.Vm.Current!.RequestId);
        });

        await Assert.That(countdown).IsEqualTo("Expiring…");
        await Assert.That(pinned).IsEqualTo("a1");
        await Assert.That(phase).IsEqualTo(ConsentPromptPhase.Resolving);
        await Assert.That(afterAck).IsEqualTo("a2"); // the ack governed, not the clock
    }

    // ---- 12: the expired-state prune advance ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Advance_on_pinned_removal_in_expired_state() {
        var (phase, afterFirstPrune, currentAfterSecond, closedOnPrune, closedAfterBeat) =
            await AvaloniaSession.DispatchAsync(() => {
                var first = Entry("a1", "p1");
                var second = Entry("a2", "p2", requestedAt: T0.AddSeconds(5));
                using var h = new PromptHarness(first, second);
                h.Activate();
                h.Tick(TimeSpan.FromSeconds(40));

                var expiredPhase = h.Vm.Phase;
                h.Prune(first);
                var advanced = h.Vm.Current!.RequestId;

                h.Prune(second);
                var onPrune = (h.Vm.Current, h.CloseRequests);

                h.Tick();
                return (expiredPhase, advanced, onPrune.Current, onPrune.CloseRequests, h.CloseRequests);
            });

        await Assert.That(phase).IsEqualTo(ConsentPromptPhase.Expired);
        await Assert.That(afterFirstPrune).IsEqualTo("a2");
        await Assert.That(currentAfterSecond).IsNull();
        // The pin releases at once (nothing dishonest stays on screen) but the CLOSE waits one
        // beat: a cache-caused emptiness may be a resubscribe's clear with its replay in flight.
        await Assert.That(closedOnPrune).IsEqualTo(0);
        await Assert.That(closedAfterBeat).IsEqualTo(1); // still empty a beat later: really empty
    }

    // ---- 12b: clear+replay must not flicker the window shut ----

    /// A resubscribe clears the cache and the daemon replays into it as two separate changesets,
    /// so an OPEN window saw the intermediate empty state, closed itself, and was then re-raised
    /// as a fresh window — pin reset, focus stolen, mid-decision. The close waits one beat, and
    /// a replay landing inside it cancels the close outright.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Clear_and_replay_within_a_beat_never_closes_the_window() {
        var (currentWhileEmpty, closedWhileEmpty, currentAfterReplay, closedAfterBeats, position) =
            await AvaloniaSession.DispatchAsync(() => {
                var first = Entry("a1", "p1");
                var second = Entry("a2", "p2", requestedAt: T0.AddSeconds(5));
                using var h = new PromptHarness(first, second);
                h.Activate();

                h.Clear();
                var empty = (h.Vm.Current, h.CloseRequests);

                h.Add(Entry("a1", "p1"));                                  // the replay: same
                h.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(5)));   // identities, fresh objects
                h.Tick();
                h.Tick();

                return (empty.Current, empty.CloseRequests, h.Vm.Current, h.CloseRequests, h.Vm.PositionText);
            });

        await Assert.That(currentWhileEmpty).IsNull();
        await Assert.That(closedWhileEmpty).IsEqualTo(0);
        await Assert.That(currentAfterReplay!.RequestId).IsEqualTo("a1"); // re-pinned on the head
        await Assert.That(closedAfterBeats).IsEqualTo(0);                 // and never closed
        await Assert.That(position).IsEqualTo("1 of 2");
    }

    /// The other half of the same rule: an emptiness that is REAL still closes the window — one
    /// beat later, not never.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Clear_with_no_replay_closes_the_window_on_the_next_beat() {
        var (closedOnClear, closedAfterBeat, closedAfterTwoBeats) = await AvaloniaSession.DispatchAsync(() => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            h.Clear();
            var onClear = h.CloseRequests;

            h.Tick();
            var afterBeat = h.CloseRequests;

            h.Tick(); // the close is one-shot: a still-empty queue never re-fires it
            return (onClear, afterBeat, h.CloseRequests);
        });

        await Assert.That(closedOnClear).IsEqualTo(0);
        await Assert.That(closedAfterBeat).IsEqualTo(1);
        await Assert.That(closedAfterTwoBeats).IsEqualTo(1);
    }

    // ---- 13: no double-submit ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Resolving_disables_all_buttons() {
        var (enabled, allowOnce, allowRemember, deny, resolveCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            var gate = h.Consent.Arm();
            var exec = h.Vm.AllowOnceCommand.Execute().ToTask();
            PromptHarness.Pump();

            var state = (h.Vm.ButtonsEnabled, CanExecute(h.Vm.AllowOnceCommand), CanExecute(h.Vm.AllowRememberCommand),
                CanExecute(h.Vm.DenyCommand), h.Consent.Resolved.Count);

            gate.SetResult(new ConsentResolveOutcome(ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested, null));
            await exec;
            PromptHarness.Pump();
            return state;
        });

        await Assert.That(enabled).IsFalse();
        await Assert.That(allowOnce).IsFalse();
        await Assert.That(allowRemember).IsFalse();
        await Assert.That(deny).IsFalse();
        await Assert.That(resolveCalls).IsEqualTo(1);
    }

    // ---- 14: cancellation ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancellation_is_a_silent_abort() {
        var (notified, current, enabled, queued, phaseText) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new PromptHarness(Entry("a1", "p1"));
            h.Activate();

            h.Consent.QueueCancellation();
            await h.Vm.DenyCommand.Execute().ToTask();
            PromptHarness.Pump();

            return (h.Notifier.Notified.ToArray(), h.Vm.Current!.RequestId, h.Vm.ButtonsEnabled, h.Vm.Queue.Count, h.Vm.PhaseText);
        });

        await Assert.That(notified).IsEmpty();
        await Assert.That(current).IsEqualTo("a1");
        await Assert.That(enabled).IsTrue();
        await Assert.That(queued).IsEqualTo(1);
        await Assert.That(phaseText).IsNull();
    }

    static bool CanExecute(ReactiveCommand<ReactiveUnit, ReactiveUnit> command) {
        var value = false;
        using var subscription = command.CanExecute.Subscribe(v => value = v); // replayed on subscribe
        return value;
    }
}

/// One ConsentPromptViewModel with every seam scripted, activated the way a window activates it.
/// Must be constructed and driven ON the headless UI thread (AvaloniaSession.DispatchAsync).
sealed class PromptHarness : IDisposable {
    public readonly FakeConsentService Consent = new();
    public readonly RecordingNotifier Notifier = new();
    public readonly FakeTicker Ticker = new();
    public readonly FakeTimeProvider Clock = new(ConsentEntries.T0);
    public readonly ConsentPromptViewModel Vm;

    readonly IDisposable _closeSub;
    IDisposable? _activation;

    public int CloseRequests { get; private set; }

    public PromptHarness(params PendingConsent[] entries) {
        Vm = new ConsentPromptViewModel(Consent, Notifier, Ticker, Clock, CancellationToken.None);
        _closeSub = Vm.CloseRequested.Subscribe(_ => CloseRequests++);
        foreach (var entry in entries) Consent.Add(entry);
    }

    public void Activate() {
        _activation = Vm.Activator.Activate();
        Pump();
    }

    public void Add(PendingConsent entry) {
        Consent.Add(entry);
        Pump();
    }

    public void Prune(PendingConsent entry) {
        Consent.Prune(entry);
        Pump();
    }

    /// The §5 Subscribed boundary: the cache is emptied and the daemon's replay re-adds. Pumped
    /// separately from the replay, because that IS the production shape — the clear and each
    /// replayed entry are independently posted changesets.
    public void Clear() {
        Consent.Clear();
        Pump();
    }

    public void Tick(TimeSpan? advance = null) {
        if (advance is { } step) Clock.Advance(step);
        Ticker.Tick();
        Pump();
    }

    /// Drains the dispatcher — every cache change reaches the ViewModel through an ObserveOn post.
    public static void Pump() => Dispatcher.UIThread.RunJobs();

    public void Dispose() {
        _activation?.Dispose();
        _closeSub.Dispose();
        Consent.Dispose();
    }
}
