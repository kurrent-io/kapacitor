using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Plain TUnit tests — ConsentService is scheduler-free (a SourceCache plus a plain Subject, no
/// Avalonia globals), so no AvaloniaSession is needed. Every settle is driven by a FIFO barrier on
/// the scripted stream, a ScriptedLocalControlOps TaskCompletionSource gate, or WaitUntilAsync
/// polling (PauseControllerTests idiom); every clock-dependent behavior is driven by
/// FakeTimeProvider — never Task.Delay-based ordering.
public class ConsentServiceTests {
    static readonly DateTimeOffset T0 = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static ConsentPendingDto Dto(
            string requestId = "a1", string promptId = "p1", string? requester = "github:1",
            string? requestedAt = null, int timeoutSeconds = 30) =>
        new(requestId, requester, "agent", "/repo", "claude", requestedAt ?? T0.ToString("O"), timeoutSeconds,
            "Alice", promptId);

    // ---- 1: subscription gate ----

    [Test]
    public async Task Subscribes_only_with_consent2_capability() {
        using var h = new ConsentHarness();

        h.Connect("consent/1");
        await Task.Delay(50); // a negative: give a would-be loop every chance to dial
        await Assert.That(h.Stream.Attempts).IsEqualTo(0);

        h.Connect("consent/1", "consent/2");
        await WaitUntilAsync(() => h.Stream.Attempts == 1, what: "the consent/2 subscribe attempt");
        h.Stream.EmitSubscribed();
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);

        // Down-level incarnation: no loop AND the retained entries (a previous incarnation's) go.
        h.Connect("consent/1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cache cleared on a down-level daemon");
        await Assert.That(h.Stream.Attempts).IsEqualTo(1);
    }

    // ---- 2: clear boundary ----

    [Test]
    public async Task Clear_happens_at_subscribed_not_before_dial() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        await h.EmitAsync(Dto());

        // Attempt 2 "fails to dial": it ends without ever reaching Subscribed.
        await h.DrainAsync();
        await h.RetryAsync();
        await h.DrainAsync();
        await Assert.That(h.View.Count).IsEqualTo(1); // still actionable — nothing was erased

        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "cache cleared at the Subscribed boundary");
    }

    // ---- 3: upsert + added signal ----

    [Test]
    public async Task Replay_upserts_by_request_id_and_entryadded_fires_on_new_keys_only() {
        using var h = new ConsentHarness();
        await h.StartAsync();

        await h.EmitAsync(Dto("a1", "p1"));
        await h.EmitAsync(Dto("a2", "p2"));
        await Assert.That(h.View.Count).IsEqualTo(2);
        await WaitUntilAsync(() => h.Added == 2, what: "two added signals");

        h.Stream.EmitPending(Dto("a1", "p1"));
        await h.DrainAsync(); // FIFO barrier: the re-push has been processed
        await Assert.That(h.View.Count).IsEqualTo(2);
        await Assert.That(h.Added).IsEqualTo(2);
    }

    // ---- 4: tombstones ----

    [Test]
    public async Task Tombstoned_prompt_id_is_dropped_and_survives_resubscribe() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("a1", "p1"));

        h.Ops.QueueResolve(ok: true, error: null);
        var outcome = await h.Service.ResolveAsync(entry, allow: true, saveRule: false, CancellationToken.None);
        await Assert.That(outcome.Kind).IsEqualTo(ConsentResolveKind.Applied);
        await Assert.That(h.View.Count).IsEqualTo(0);

        h.Stream.EmitPending(Dto("a1", "p1"));
        await h.DrainAsync();
        await Assert.That(h.View.Count).IsEqualTo(0); // ghost replay dropped

        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        h.Stream.EmitPending(Dto("a1", "p1"));
        await h.DrainAsync();
        await Assert.That(h.View.Count).IsEqualTo(0); // STILL dropped: tombstones are service-lifetime

        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        var successor = await h.EmitAsync(Dto("a1", "p2")); // different identity under the same key
        await Assert.That(successor.PromptId).IsEqualTo("p2");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    // ---- 5: identity-guarded eviction ----

    [Test]
    public async Task Conclusive_ack_evicts_by_identity_including_a_replayed_fresh_instance() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var original = await h.EmitAsync(Dto("a1", "p1"));

        var gate = h.Ops.ArmResolve();
        var resolve = h.Service.ResolveAsync(original, allow: true, saveRule: false, CancellationToken.None);
        await WaitUntilAsync(() => h.Ops.ResolveCalls == 1, what: "the resolve to reach the ops layer");

        // A reconnect clears and replays the SAME identity as a brand-new object.
        await h.DrainAsync();
        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "the Subscribed clear");
        var replayed = await h.EmitAsync(Dto("a1", "p1"));
        await Assert.That(ReferenceEquals(replayed, original)).IsFalse();

        gate.SetResult(new ConsentAckDto(true, null, null));
        var outcome = await resolve;

        await Assert.That(outcome.Kind).IsEqualTo(ConsentResolveKind.Applied);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    // ---- 6: ABA defense ----

    [Test]
    public async Task Successor_with_same_request_id_survives_predecessors_ack() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var predecessor = await h.EmitAsync(Dto("a1", "p1"));

        var gate = h.Ops.ArmResolve();
        var resolve = h.Service.ResolveAsync(predecessor, allow: true, saveRule: false, CancellationToken.None);
        await WaitUntilAsync(() => h.Ops.ResolveCalls == 1, what: "the resolve to reach the ops layer");

        var successor = await h.EmitAsync(Dto("a1", "p2"));

        gate.SetResult(new ConsentAckDto(false, "already decided", null));
        var outcome = await resolve;

        await Assert.That(outcome.Kind).IsEqualTo(ConsentResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(1);
        await Assert.That(h.View.Lookup("a1").Value.PromptId).IsEqualTo(successor.PromptId);
    }

    // ---- 7: ack -> outcome mapping ----

    [Test]
    [Arguments(true, null, null, false, ConsentResolveKind.Applied, ConsentRuleOutcome.NotRequested)]
    [Arguments(true, null, true, true, ConsentResolveKind.Applied, ConsentRuleOutcome.Saved)]
    [Arguments(true, "store full", false, true, ConsentResolveKind.AppliedRuleRejected, ConsentRuleOutcome.Rejected)]
    [Arguments(true, null, null, true, ConsentResolveKind.Applied, ConsentRuleOutcome.Saved)] // old-format ack carve-out
    [Arguments(false, null, true, true, ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.Saved)]
    [Arguments(false, null, null, true, ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.Unknown)]
    [Arguments(false, null, null, false, ConsentResolveKind.AlreadyDecided, ConsentRuleOutcome.NotRequested)]
    public async Task Resolve_outcome_mapping(
            bool ok, string? error, bool? ruleSaved, bool saveRule,
            ConsentResolveKind expectedKind, ConsentRuleOutcome expectedRule) {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto());

        h.Ops.QueueResolve(ok, error, ruleSaved);
        var outcome = await h.Service.ResolveAsync(entry, allow: true, saveRule, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(expectedKind);
        await Assert.That(outcome.RuleOutcome).IsEqualTo(expectedRule);
        await Assert.That(outcome.Error).IsEqualTo(error);
        await Assert.That(h.View.Count).IsEqualTo(0); // every ack is conclusive: remove + tombstone
    }

    // ---- 8: save_rule guard ----

    [Test]
    public async Task Save_rule_guard_null_and_empty_requester() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var noRequester = await h.EmitAsync(Dto("a1", "p1", requester: null));
        var emptyRequester = await h.EmitAsync(Dto("a2", "p2", requester: ""));
        var named = await h.EmitAsync(Dto("a3", "p3", requester: "github:2821205"));

        h.Ops.QueueResolve(ok: true, error: null);
        var first = await h.Service.ResolveAsync(noRequester, allow: true, saveRule: true, CancellationToken.None);
        h.Ops.QueueResolve(ok: true, error: null);
        var second = await h.Service.ResolveAsync(emptyRequester, allow: true, saveRule: true, CancellationToken.None);
        h.Ops.QueueResolve(ok: true, error: null, ruleSaved: true);
        var third = await h.Service.ResolveAsync(named, allow: true, saveRule: true, CancellationToken.None);

        // A null requester would serialize into a wildcard allow-everything rule: never sent.
        await Assert.That(h.Ops.ResolvePayloads[0].SaveRule).IsNull();
        await Assert.That(h.Ops.ResolvePayloads[1].SaveRule).IsNull();
        await Assert.That(first.Kind).IsEqualTo(ConsentResolveKind.RuleSkippedNoRequester);
        await Assert.That(first.RuleOutcome).IsEqualTo(ConsentRuleOutcome.SkippedNoRequester);
        await Assert.That(second.Kind).IsEqualTo(ConsentResolveKind.RuleSkippedNoRequester);
        await Assert.That(second.RuleOutcome).IsEqualTo(ConsentRuleOutcome.SkippedNoRequester);

        await Assert.That(h.Ops.ResolvePayloads[2].SaveRule)
            .IsEqualTo(new ConsentRuleDto("allow", "github:2821205", null, null, null));
        await Assert.That(third.Kind).IsEqualTo(ConsentResolveKind.Applied);
        await Assert.That(third.RuleOutcome).IsEqualTo(ConsentRuleOutcome.Saved);
    }

    // ---- 9: identity echo ----

    [Test]
    public async Task Resolve_sends_the_targets_exact_prompt_id() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("agent-7", "prompt-7"));

        h.Ops.QueueResolve(ok: true, error: null);
        await h.Service.ResolveAsync(entry, allow: false, saveRule: false, CancellationToken.None);

        var sent = h.Ops.ResolvePayloads[0];
        await Assert.That(sent.PromptId).IsEqualTo(entry.PromptId);
        await Assert.That(sent.RequestId).IsEqualTo(entry.RequestId);
        await Assert.That(sent.Decision).IsEqualTo("deny");
    }

    // ---- 10: transport failure ----

    [Test]
    public async Task Transport_failure_keeps_the_entry_and_refreshes_prune_after() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto());

        h.Ops.QueueResolveFailure("daemon_unreachable");
        var outcome = await h.Service.ResolveAsync(entry, allow: true, saveRule: false, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(ConsentResolveKind.TransportFailure);
        await Assert.That(outcome.Error).IsEqualTo("daemon_unreachable");
        await Assert.That(h.View.Count).IsEqualTo(1);
        await Assert.That(h.View.Lookup("a1").Value.PruneAfter)
            .IsEqualTo(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(5));
    }

    // An unmapped exception (LocalControlOps does not classify every socket-construction failure)
    // must not reach the UI command, must keep the unsettled entry, and must free the lane —
    // the PauseController/AgentActionService discipline.
    [Test]
    public async Task Unmapped_exception_is_contained_and_keeps_the_entry() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto());

        h.Ops.QueueResolveUnmappedFailure(new InvalidOperationException("boom"));
        var outcome = await h.Service.ResolveAsync(entry, allow: true, saveRule: false, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(ConsentResolveKind.TransportFailure);
        await Assert.That(outcome.Error).IsEqualTo("boom");
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Ops.QueueResolve(ok: true, error: null);
        var retry = await h.Service.ResolveAsync(entry, allow: true, saveRule: false, CancellationToken.None);
        await Assert.That(retry.Kind).IsEqualTo(ConsentResolveKind.Applied); // lane freed
    }

    // ---- 11: cancellation ----

    [Test]
    public async Task Cancellation_propagates_and_keeps_the_entry() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Service.ResolveAsync(entry, allow: true, saveRule: false, cts.Token));

        await Assert.That(h.View.Count).IsEqualTo(1);

        // No tombstone: after a clearing resubscribe the same identity is admitted again.
        await h.DrainAsync();
        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "the Subscribed clear");
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    // The OTHER cancellation shape: cancelled while the ops call is in flight, so the OCE surfaces
    // from INSIDE the resolve's try — the one place the unmapped-exception arm could swallow it and
    // fabricate a TransportFailure settlement out of a caller's abort.
    [Test]
    public async Task Cancellation_in_flight_propagates_and_keeps_the_entry() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto());
        using var cts = new CancellationTokenSource();

        h.Ops.ArmResolve(); // never completed: the token, not the ack, ends this call
        var resolve = h.Service.ResolveAsync(entry, allow: true, saveRule: false, cts.Token);
        await WaitUntilAsync(() => h.Ops.ResolveCalls == 1, what: "the resolve to reach the ops layer");
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolve);
        await Assert.That(h.View.Count).IsEqualTo(1);

        // No tombstone: after a clearing resubscribe the same identity is admitted again.
        await h.DrainAsync();
        await h.RetryAsync();
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "the Subscribed clear");
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    // ---- 12: prune ----

    [Test]
    public async Task Prune_removes_past_prune_after_but_skips_the_inflight_target() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        var expired = await h.EmitAsync(Dto("a1", "p1", requestedAt: h.Clock.GetUtcNow().ToString("O")));
        var inFlight = await h.EmitAsync(Dto("a2", "p2", requestedAt: h.Clock.GetUtcNow().ToString("O")));
        await Assert.That(expired.PruneAfter).IsEqualTo(T0 + TimeSpan.FromSeconds(35));

        var gate = h.Ops.ArmResolve();
        var resolve = h.Service.ResolveAsync(inFlight, allow: true, saveRule: false, CancellationToken.None);
        await WaitUntilAsync(() => h.Ops.ResolveCalls == 1, what: "the resolve to reach the ops layer");

        h.Clock.Advance(TimeSpan.FromSeconds(40)); // both entries are now past PruneAfter
        h.Ticker.Tick();

        await Assert.That(h.View.Count).IsEqualTo(1);
        await Assert.That(h.View.Lookup("a2").HasValue).IsTrue(); // the in-flight target is skipped

        gate.SetResult(new ConsentAckDto(true, null, null));
        await resolve;
        await Assert.That(h.View.Count).IsEqualTo(0); // evicted by conclusion, never double-removed
    }

    // ---- 13/14: loop lifecycle ----

    [Test]
    public async Task Stream_end_while_connected_retries_after_1s() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        await h.DrainAsync();

        await WaitUntilAsync(() => h.Time.TimersCreated == 1, what: "the 1s retry delay to be armed");
        await Assert.That(h.Stream.Attempts).IsEqualTo(1); // no retry before the clock moves

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "the resubscribe");
    }

    [Test]
    public async Task Leaving_connected_cancels_the_loop_and_retains_entries() {
        using var h = new ConsentHarness();
        await h.StartAsync();
        await h.EmitAsync(Dto());

        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await WaitUntilAsync(() => h.Stream.Ended == 1, what: "the loop to unwind");

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        await Assert.That(h.Stream.Attempts).IsEqualTo(1); // no resubscribe while disconnected
        await Assert.That(h.View.Count).IsEqualTo(1);      // the daemon may still hold live prompts
    }

    // ---- 15: deadline hint ----

    [Test]
    public async Task Deadline_hint_falls_back_on_unparseable_requested_at() {
        using var h = new ConsentHarness();
        await h.StartAsync();

        var garbage = await h.EmitAsync(Dto("a1", "p1", requestedAt: "garbage", timeoutSeconds: 45));
        await Assert.That(garbage.DeadlineHint).IsEqualTo(T0 + TimeSpan.FromSeconds(45)); // arrival + timeout

        var stamped = await h.EmitAsync(
            Dto("a2", "p2", requestedAt: (T0 - TimeSpan.FromSeconds(10)).ToString("O"), timeoutSeconds: 45));
        await Assert.That(stamped.DeadlineHint).IsEqualTo(T0 + TimeSpan.FromSeconds(35));
    }
}

/// One scripted consent subscription attempt per RunAsync call, fed from one FIFO channel that
/// outlives the attempts. `Attempts`/`Ended` make the loop's retry cadence and its unwinding
/// observable; a null item is the end-of-attempt marker, which doubles as a barrier proving every
/// earlier item was processed.
sealed class FakeConsentStream {
    readonly Channel<ConsentStreamEvent?> _channel = Channel.CreateUnbounded<ConsentStreamEvent?>();

    int _attempts;
    int _ended;

    public int Attempts => Volatile.Read(ref _attempts);
    public int Ended    => Volatile.Read(ref _ended);

    public async IAsyncEnumerable<ConsentStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
        Interlocked.Increment(ref _attempts);
        try {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        } finally {
            Interlocked.Increment(ref _ended);
        }
    }

    public void EmitSubscribed() => _channel.Writer.TryWrite(new ConsentStreamEvent.Subscribed());
    public void EmitPending(ConsentPendingDto dto) => _channel.Writer.TryWrite(new ConsentStreamEvent.Pending(dto));
    public void EndAttempt() => _channel.Writer.TryWrite(null);
}

/// Delegates to a FakeTimeProvider while counting CreateTimer calls, so a test can wait for the
/// loop's retry delay to be ARMED before advancing the clock — advancing first would leave the
/// timer scheduled a second into a future that already passed (LaunchConsentGateTests idiom).
sealed class TimerCountingTimeProvider(FakeTimeProvider inner) : TimeProvider {
    int _timersCreated;
    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override DateTimeOffset GetUtcNow()   => inner.GetUtcNow();
    public override long GetTimestamp()          => inner.GetTimestamp();
    public override TimeZoneInfo LocalTimeZone   => inner.LocalTimeZone;
    public override long TimestampFrequency      => inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        Interlocked.Increment(ref _timersCreated);
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}

/// One ConsentService with every seam scripted, plus an observable-cache view of the pending
/// cache and a counter for the added signal.
sealed class ConsentHarness : IDisposable {
    public static readonly DateTimeOffset Start = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    public readonly FakeDaemonClientService Daemon = new();
    public readonly ScriptedLocalControlOps Ops    = new();
    public readonly FakeTicker Ticker              = new();
    public readonly FakeConsentStream Stream       = new();
    public readonly FakeTimeProvider Clock         = new(Start);
    public readonly TimerCountingTimeProvider Time;
    public readonly ConsentService Service;
    public readonly IObservableCache<PendingConsent, string> View;

    readonly IDisposable _addedSub;
    int _added;
    int _timersSeen;

    public ConsentHarness() {
        Time      = new TimerCountingTimeProvider(Clock);
        Service   = new ConsentService(Daemon, Ops, Ticker, Stream.RunAsync, Time, CancellationToken.None);
        View      = Service.Pending.AsObservableCache();
        _addedSub = Service.EntryAdded.Subscribe(_ => Interlocked.Increment(ref _added));
    }

    public int Added => Volatile.Read(ref _added);

    public void Connect(params string[] capabilities) =>
        Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, capabilities));

    /// Connect with consent/2, wait for the dial, and cross the Subscribed boundary.
    public async Task StartAsync() {
        Connect("consent/1", "consent/2");
        await WaitAsync(() => Stream.Attempts == 1, "the first subscribe attempt");
        Stream.EmitSubscribed();
    }

    public async Task<PendingConsent> EmitAsync(ConsentPendingDto dto) {
        Stream.EmitPending(dto);
        await WaitAsync(
            () => View.Lookup(dto.RequestId) is { HasValue: true, Value.PromptId: var id } && id == dto.PromptId,
            $"pending {dto.RequestId}/{dto.PromptId} to be cached");
        return View.Lookup(dto.RequestId).Value;
    }

    /// Ends the current attempt — a FIFO barrier proving every earlier emission was processed.
    public async Task DrainAsync() {
        var ended = Stream.Ended;
        Stream.EndAttempt();
        await WaitAsync(() => Stream.Ended > ended, "the attempt to end");
    }

    /// Fires the loop's 1s retry delay once the delay is actually armed, then waits for the dial.
    public async Task RetryAsync() {
        var attempts = Stream.Attempts;
        await WaitAsync(() => Time.TimersCreated > _timersSeen, "the retry delay to be armed");
        _timersSeen = Time.TimersCreated;
        Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => Stream.Attempts > attempts, "the resubscribe");
    }

    static async Task WaitAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    public void Dispose() {
        _addedSub.Dispose();
        Service.Dispose();
        View.Dispose();
    }
}
