using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LaunchConsentBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    /// <summary>Bounds an otherwise-unbounded wait so a broken invariant FAILS with a named assertion
    /// instead of hanging the suite — same contract as the identically-named helper in
    /// OneExecutionDomainProcessorTests/OneExecutionDomainTests.</summary>
    static async Task<T> WaitBounded<T>(Task<T> task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        return await task;
    }

    static LaunchConsentPromptRequest Req(string id = "a1", string promptId = "p1") =>
        new(id, "user_x", "agent", "/tmp/repo", "claude", DateTimeOffset.UtcNow.ToString("O"), 5, null, promptId);

    static LaunchConsentPromptRequest ReqWithPromptId(string id, string promptId) => Req(id, promptId);

    [Test]
    public async Task No_subscriber_reports_HasSubscriber_false() {
        var broker = new LaunchConsentBroker();
        await Assert.That(broker.HasSubscriber).IsFalse();
        var (id, _) = broker.Subscribe();
        await Assert.That(broker.HasSubscriber).IsTrue();
        broker.Unsubscribe(id);
        await Assert.That(broker.HasSubscriber).IsFalse();
    }

    [Test]
    public async Task Prompt_delivers_to_subscriber_and_resolution_completes_it() {
        var broker = new LaunchConsentBroker();
        var (_, reader) = broker.Subscribe();
        var pending = broker.PromptAsync(Req(), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        var delivered = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(delivered.RequestId).IsEqualTo("a1");
        await Assert.That(broker.TryResolve("a1", allow: true)).IsTrue();
        await Assert.That(await pending).IsTrue();
        await Assert.That(broker.TryResolve("a1", allow: true)).IsFalse(); // already resolved
        await Assert.That(reader.TryRead(out _)).IsFalse(); // no duplicate item queued
    }

    [Test]
    public async Task Prompt_times_out_to_null() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req(), TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Timeout_claims_the_entry_so_a_later_TryResolve_reports_false() {
        // Ok=true on the IPC ack must guarantee the decision applied — so once the timeout has
        // won the race and denied the launch, a resolver arriving after must be told it lost
        // (TryResolve=false), never allowed to silently "apply" to an already-decided launch.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req("a-timeout"), TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None);
        await Assert.That(result).IsNull();
        await Assert.That(broker.TryResolve("a-timeout", allow: true)).IsFalse();
    }

    [Test]
    public async Task Successor_prompt_reusing_the_same_request_id_resolves_independently_of_a_predecessor() {
        // NOTE (honest limitation): this is a same-id REUSE test, not a live reproduction of the
        // ABA race the instance-scoped cleanup guards against (a successor's TryAdd landing in the
        // narrow window between a predecessor's claim and that predecessor's own cleanup running).
        // That exact interleaving isn't deterministically reproducible in-process. What this DOES
        // pin: a same-id successor added after a predecessor's full lifecycle (claimed, resolved,
        // its own cleanup already run) is resolvable on its own terms — the structural guarantee
        // (instance-scoped, never key-scoped, removal) is what actually closes the race; this test
        // is a smoke check that reuse doesn't regress, not a race reproduction.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();

        var promptA = broker.PromptAsync(Req("dup-id"), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Assert.That(broker.TryResolve("dup-id", allow: true)).IsTrue();
        await Assert.That(await promptA).IsTrue();

        var promptB = broker.PromptAsync(Req("dup-id"), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Assert.That(broker.PendingSnapshot().Any(r => r.RequestId == "dup-id")).IsTrue();
        await Assert.That(broker.TryResolve("dup-id", allow: true)).IsTrue();
        await Assert.That(await promptB).IsTrue();
    }

    [Test]
    public async Task Late_subscriber_receives_pending_snapshot_replay() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe(); // HasSubscriber must be true for the gate to even prompt
        var pending = broker.PromptAsync(Req("a2"), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
        var (_, lateReader) = broker.Subscribe();
        var replayed = await lateReader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(replayed.RequestId).IsEqualTo("a2");
        broker.TryResolve("a2", false);
        await Assert.That(await pending).IsFalse();
    }

    // ══ WaitForSubscriberAsync — the generational waiter state machine (spec §3.2). All timing
    // driven by FakeTimeProvider; no real sleeps stand in for timeout semantics. ═══════════════

    [Test]
    public async Task WaitForSubscriber_returns_true_synchronously_when_a_subscriber_is_already_present() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var time = new FakeTimeProvider();
        var result = await broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task WaitForSubscriber_two_concurrent_waiters_both_complete_true_on_one_Subscribe() {
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();

        // Neither waiter has suspended on anything but the shared arrival source yet — the
        // synchronous prefix of WaitForSubscriberAsync (the gate check + capture) runs to
        // completion before either call returns a Task, so both are guaranteed to have captured
        // the SAME zero-subscriber-generation source before Subscribe() runs below.
        var waiter1 = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, CancellationToken.None);
        var waiter2 = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, CancellationToken.None);

        broker.Subscribe();

        await Assert.That(await waiter1).IsTrue();
        await Assert.That(await waiter2).IsTrue();
    }

    [Test]
    public async Task WaitForSubscriber_one_waiters_timeout_does_not_disturb_a_second_waiter() {
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();

        var waiter1 = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(1), time, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await waiter1).IsFalse();

        // A second waiter started after the first's timeout still shares the SAME generation (no
        // subscriber ever arrived, so nothing re-armed it) — its own timeout must be unaffected by
        // the first waiter's expiry, and it must still see a Subscribe() that lands afterward.
        var waiter2 = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, CancellationToken.None);
        broker.Subscribe();
        await Assert.That(await waiter2).IsTrue();
    }

    [Test]
    public async Task WaitForSubscriber_after_subscribe_then_unsubscribe_a_new_wait_blocks_on_a_fresh_generation() {
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();

        var (id, _) = broker.Subscribe(); // completes the construction-time arrival source
        broker.Unsubscribe(id);           // 1→0: re-arms a fresh, incomplete source

        // If the stale completed source leaked forward, this would resolve true immediately
        // instead of riding out the full timeout to false.
        var waiter = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(2), time, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(await waiter).IsFalse();
    }

    [Test]
    public async Task Unsubscribe_of_an_unknown_id_while_empty_is_a_0_to_0_noop_and_does_not_orphan_a_pending_waiter() {
        // A no-op Unsubscribe (unknown/duplicate id on an already-empty subscriber map) must NOT
        // re-arm the arrival source — that would be a fresh instance the waiter below never
        // learns about, forcing it to burn its whole wait budget instead of completing true the
        // moment Subscribe() lands.
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();

        var waiter = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, CancellationToken.None);
        broker.Unsubscribe(Guid.NewGuid()); // unknown id; _subscribers was already empty

        broker.Subscribe();
        await Assert.That(await WaitBounded(waiter, "a 0-to-0 Unsubscribe no-op orphaned the pending waiter — it never completed"))
            .IsTrue();
    }

    [Test]
    public async Task WaitForSubscriber_expiry_and_subscribe_race_arrival_wins() {
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();
        var budget = TimeSpan.FromSeconds(5);

        var waiter = broker.WaitForSubscriberAsync(budget, time, CancellationToken.None);

        // A second fake timer, due one millisecond before the waiter's own timeout, fires
        // Subscribe(). FakeTimeProvider dispatches due timers in due-time order within a single
        // Advance() call, so this pins Subscribe()'s dictionary mutation as happening-before the
        // waiter's timeout — whichever way the runtime resolves the composed WaitAsync (a clean
        // arrival vs. a TimeoutException that then hits the recheck), the recheck can only ever
        // observe a non-empty subscriber set. That is "arrival wins ties", made deterministic
        // instead of a hardware race between two real threads.
        using var trigger = time.CreateTimer(
            _ => broker.Subscribe(), null, budget - TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

        time.Advance(budget);

        await Assert.That(await waiter).IsTrue();
    }

    [Test]
    public async Task WaitForSubscriber_external_cancellation_propagates_as_OperationCanceledException() {
        var broker = new LaunchConsentBroker();
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var waiter = broker.WaitForSubscriberAsync(TimeSpan.FromSeconds(30), time, cts.Token);
        await cts.CancelAsync();

        await Assert.That(async () => await waiter).Throws<OperationCanceledException>();
    }

    // ══ PromptAsync deadline discipline (spec §3.2) — TimeProvider-driven timeout mechanics
    // replacing the CancellationTokenSource.CancelAfter linked-CTS, plus the Cancellation
    // paragraph's external-ct-must-propagate contract. ═══════════════════════════════════════

    [Test]
    public async Task PromptAsync_timeout_is_driven_by_the_injected_TimeProvider_not_the_system_clock() {
        // A 5-SECOND timeout that resolves inside a fast unit test (no real waiting) proves the
        // fake clock — not the system clock — drives PromptAsync's own timeout.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var time = new FakeTimeProvider();

        var promptTask = broker.PromptAsync(Req("a-fake-timeout"), TimeSpan.FromSeconds(5), time, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));

        await Assert.That(await promptTask).IsNull();
    }

    [Test]
    public async Task PromptAsync_with_zero_timeout_settles_immediately_to_null() {
        // Spec §3.2: zero remaining is not a special case — PromptAsync runs with a zero budget
        // and settles as the standard timeout denial, no separate code path.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req("a-zero-timeout"), TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task PromptAsync_external_cancellation_propagates_and_claims_the_entry() {
        // The caller's own token firing (daemon shutdown / launch teardown) must propagate as
        // OperationCanceledException, never resolve to a null timeout denial — and it claims the
        // entry the same way a timeout would, so a resolver racing in afterward is told it lost.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        using var cts = new CancellationTokenSource();

        var promptTask = broker.PromptAsync(Req("a-external-cancel"), TimeSpan.FromSeconds(30), TimeProvider.System, cts.Token);
        await cts.CancelAsync();

        await Assert.That(async () => await promptTask).Throws<OperationCanceledException>();
        await Assert.That(broker.TryResolve("a-external-cancel", allow: true)).IsFalse();
    }

    // ══ TryResolve promptId-echo claim (spec §4.1) — the atomic identity-checked overload. ═════

    [Test]
    public async Task TryResolve_with_matching_echo_resolves_and_mismatch_leaves_pending_untouched() {
        var broker = new LaunchConsentBroker();
        var (id, _) = broker.Subscribe();
        var promptTask = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);

        await Assert.That(broker.TryResolve("agent-1", true, "WRONG")).IsFalse();   // mismatch: no claim
        await Assert.That(promptTask.IsCompleted).IsFalse();                        // A still pending
        await Assert.That(broker.TryResolve("agent-1", true, "p-A")).IsTrue();      // exact echo claims
        await Assert.That(await promptTask).IsTrue();
        broker.Unsubscribe(id);
    }

    [Test]
    public async Task Stale_echo_for_a_superseded_request_never_decides_the_successor() {
        // A times out; successor B reuses the id; A's late resolve must answer false and leave B live.
        var broker = new LaunchConsentBroker();
        var (id, _) = broker.Subscribe();
        var a = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        await Assert.That(await a).IsNull(); // timeout claimed A
        var b = broker.PromptAsync(ReqWithPromptId("agent-1", "p-B"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);

        await Assert.That(broker.TryResolve("agent-1", true, "p-A")).IsFalse(); // stale identity: refused
        await Assert.That(b.IsCompleted).IsFalse();                             // B untouched
        await Assert.That(broker.TryResolve("agent-1", false, "p-B")).IsTrue(); // B decided on its own terms
        await Assert.That(await b).IsFalse();
        broker.Unsubscribe(id);
    }

    [Test]
    public async Task Null_echo_preserves_legacy_resolve_by_id() {
        var broker = new LaunchConsentBroker();
        var (id, _) = broker.Subscribe();
        var a = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);
        await Assert.That(broker.TryResolve("agent-1", true, null)).IsTrue();
        await Assert.That(await a).IsTrue();
        broker.Unsubscribe(id);
    }
}
