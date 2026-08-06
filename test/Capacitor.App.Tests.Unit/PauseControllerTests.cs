using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Plain TUnit tests — no Avalonia session, nothing touches Avalonia/Rx globals (the controller
/// is scheduler-free). Every async settle is driven by ScriptedLocalControlOps's per-call
/// TaskCompletionSource gates and WaitUntilAsync polling (DaemonClientServiceTests idiom) —
/// never Task.Delay-based ordering.
public class PauseControllerTests {
    static readonly ConsentRuleDto PauseRule = new("deny", null, null, null, null);

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    // ---- initial refresh ----

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task First_refresh_sets_verified_checked(bool hasPauseRuleAtZero) {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(hasPauseRuleAtZero
            ? new ConsentPolicyDto("prompt", 30, [PauseRule])
            : new ConsentPolicyDto("prompt", 30, []));

        controller.RequestRefresh();
        await WaitUntilAsync(() => states.Count >= 2, what: "refresh to settle");

        await Assert.That(states[^1]).IsEqualTo(new PauseState(hasPauseRuleAtZero, true, false));
        await Assert.That(notifications).IsEmpty();
    }

    // Spec §6/§12 "detection strictness": an all-wildcard deny at an index OTHER than 0 must not
    // check the toggle — pins HasPauseRuleAtZero against a Rules.Any(...)-shaped regression that
    // would otherwise pass every other test in this file.
    [Test]
    public async Task Wildcard_deny_at_nonzero_index_is_not_paused() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var narrower = new ConsentRuleDto("deny", "someone", null, null, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [narrower, PauseRule]));

        controller.RequestRefresh();
        await WaitUntilAsync(() => states.Count >= 2, what: "refresh to settle");

        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, true, false));
    }

    [Test]
    public async Task Refresh_failure_marks_unverified() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule]));
        controller.RequestRefresh();
        await WaitUntilAsync(() => states.Count >= 2, what: "first refresh to settle");
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));

        ops.QueueGetFailure("unexpected_reply");
        controller.RequestRefresh();
        await WaitUntilAsync(() => states.Count >= 3, what: "failing refresh to settle");

        // Checked is RETAINED from the last successful refresh; only Verified flips.
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, false, false));
        await Assert.That(notifications).IsEmpty(); // passive failures never notify — stderr only
    }

    [Test]
    public async Task Unmapped_exception_still_releases_the_lane() {
        // Reachable in production: an over-long UnixDomainSocketEndPoint path throws
        // ArgumentOutOfRangeException, which LocalControlOps.ExchangeAsync does not classify —
        // any such exception must still release the lane, or every later RequestRefresh/
        // RequestToggle is silently dropped/ignored forever with no banner and no log line.
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGetUnmappedFailure(new InvalidOperationException("boom"));
        controller.RequestRefresh();
        await WaitUntilAsync(() => states.Count >= 2, what: "refresh to settle after an unmapped exception");

        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, false, false)); // unverified; no mapped copy exists
        await Assert.That(notifications).IsEmpty(); // unmapped exceptions log to stderr only, never notify

        // The lane-freed proof: a subsequent refresh is ACCEPTED (issues a Get), not dropped.
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule]));
        controller.RequestRefresh();
        await WaitUntilAsync(() => ops.GetCalls >= 2, what: "lane to accept a subsequent refresh");
        await WaitUntilAsync(() => states.Count >= 3, what: "subsequent refresh to settle");
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    [Test]
    public async Task Passive_dropped_while_busy() {
        var ops = new ScriptedLocalControlOps();
        using var controller = new PauseController(ops, _ => { }, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var gate = ops.ArmGet();
        controller.RequestRefresh();
        await WaitUntilAsync(() => ops.GetCalls >= 1, what: "first refresh Get to be issued");

        controller.RequestRefresh(); // lane already Passive: dropped synchronously, no new Get
        await Assert.That(ops.GetCalls).IsEqualTo(1);

        gate.SetResult(new ConsentPolicyDto("prompt", 30, []));
        await WaitUntilAsync(() => states.Count >= 2, what: "refresh to settle");
        await Assert.That(ops.GetCalls).IsEqualTo(1); // exactly ONE Get ever issued
    }

    // ---- toggle payloads ----

    [Test]
    public async Task Toggle_pause_puts_rule_at_zero() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var narrower = new ConsentRuleDto("deny", "someone", null, null, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 45, [narrower]));
        ops.QueueAck(true, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 45, [PauseRule, narrower]));

        controller.RequestToggle(true);
        await Assert.That(states[^1].Busy).IsTrue(); // pushed synchronously before RequestToggle returns
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(ops.PutCalls).IsEqualTo(1);
        var put = ops.PutPayloads[0];
        await Assert.That(put.Default).IsEqualTo("prompt");
        await Assert.That(put.PromptTimeoutSeconds).IsEqualTo(45);
        await Assert.That(put.Rules).IsEquivalentTo([PauseRule, narrower], CollectionOrdering.Matching);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
        await Assert.That(notifications).IsEmpty();
    }

    [Test]
    public async Task Toggle_unpause_removes_only_index_zero() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var narrower = new ConsentRuleDto("deny", "someone", null, null, null);
        ops.QueueGet(new ConsentPolicyDto("allow", 30, [PauseRule, narrower]));
        ops.QueueAck(true, null);
        ops.QueueGet(new ConsentPolicyDto("allow", 30, [narrower]));

        controller.RequestToggle(false);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(ops.PutCalls).IsEqualTo(1);
        var put = ops.PutPayloads[0];
        await Assert.That(put.Default).IsEqualTo("allow");
        await Assert.That(put.PromptTimeoutSeconds).IsEqualTo(30);
        await Assert.That(put.Rules).IsEquivalentTo([narrower], CollectionOrdering.Matching);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, true, false));
    }

    [Test]
    public async Task Toggle_idempotent_no_put() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule])); // already at desired state
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule])); // trailing refresh

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(ops.PutCalls).IsEqualTo(0);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    // ---- lane serialization ----

    [Test]
    public async Task Toggle_during_passive_queues_desired_idempotent_when_rule_already_present() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var passiveGate = ops.ArmGet();
        controller.RequestRefresh();
        await WaitUntilAsync(() => ops.GetCalls >= 1, what: "passive Get to be issued");

        controller.RequestToggle(true); // queued: passive owns the lane
        await Assert.That(states[^1].Busy).IsTrue(); // pushed synchronously, before the passive even resolves

        // The rule appeared externally while the passive read was in flight — the toggle's own
        // fresh Get (below) must see it too, since neither Get was reused across the two ops.
        var withRule = new ConsentPolicyDto("prompt", 30, [PauseRule]);
        ops.QueueGet(withRule); // the queued toggle's OWN Get
        ops.QueueGet(withRule); // its trailing refresh

        passiveGate.SetResult(withRule);
        await WaitUntilAsync(() => states.Count >= 4, what: "queued toggle to settle");

        await Assert.That(ops.PutCalls).IsEqualTo(0); // idempotent — never inverted
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    [Test]
    public async Task Toggle_during_passive_queues_desired_puts_when_rule_absent() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var passiveGate = ops.ArmGet();
        controller.RequestRefresh();
        await WaitUntilAsync(() => ops.GetCalls >= 1, what: "passive Get to be issued");

        controller.RequestToggle(true); // queued: passive owns the lane
        await Assert.That(states[^1].Busy).IsTrue();

        var withoutRule = new ConsentPolicyDto("prompt", 30, []);
        ops.QueueGet(withoutRule);              // the queued toggle's OWN Get
        ops.QueueAck(true, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule])); // trailing refresh

        passiveGate.SetResult(withoutRule);
        await WaitUntilAsync(() => states.Count >= 4, what: "queued toggle to settle");

        await Assert.That(ops.PutCalls).IsEqualTo(1);
        await Assert.That(ops.PutPayloads[0].Rules).IsEquivalentTo([PauseRule], CollectionOrdering.Matching);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    [Test]
    public async Task Toggle_during_toggle_ignored() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var initialGate = ops.ArmGet();
        controller.RequestToggle(true);
        await WaitUntilAsync(() => ops.GetCalls >= 1, what: "toggle Get to be issued");

        controller.RequestToggle(false); // a toggle already owns the lane: ignored
        await Assert.That(ops.GetCalls).IsEqualTo(1);
        await Assert.That(ops.PutCalls).IsEqualTo(0);

        // Pre-arm what the FIRST (desired: true) toggle needs before releasing it, so there is
        // no race between this test thread and the controller's own continuation.
        ops.QueueAck(true, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule])); // trailing refresh
        initialGate.SetResult(new ConsentPolicyDto("prompt", 30, [])); // no rule yet -> desired true -> Put

        await WaitUntilAsync(() => states.Count >= 3, what: "original toggle to settle");

        await Assert.That(ops.GetCalls).IsEqualTo(2); // the ignored second RequestToggle never issued a Get
        await Assert.That(ops.PutCalls).IsEqualTo(1);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false)); // the FIRST desired value won
    }

    // ---- ack handling ----

    [Test]
    [Arguments("custom rejection text", "custom rejection text")]
    [Arguments(null, "The daemon rejected the change")]
    [Arguments("", "The daemon rejected the change")]
    public async Task Ack_error_notifies(string? ackError, string expectedNotification) {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(new ConsentPolicyDto("prompt", 30, []));
        ops.QueueAck(false, ackError);
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule])); // trailing refresh still succeeds

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(notifications).IsEquivalentTo([expectedNotification], CollectionOrdering.Matching);
        // A successful trailing refresh still reconciles the checkmark despite the ack failure.
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    [Test]
    public async Task Ack_warning_success() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(new ConsentPolicyDto("prompt", 30, []));
        ops.QueueAck(true, "applied with a caveat"); // ok:true + non-null error is still success
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule]));

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(notifications).IsEmpty(); // warning goes to stderr only, never a banner
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));
    }

    // ---- trailing refresh contract ----

    [Test]
    public async Task Trailing_refresh_reconciles() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [])); // initial: no rule
        ops.QueueAck(true, null);
        // A concurrent CLI edit removed the rule again before the trailing read — proving
        // reconciliation reflects the TRAILING read, never an assumption from the Put payload.
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, []));

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, true, false));
        await Assert.That(notifications).IsEmpty();
    }

    [Test]
    public async Task Trailing_refresh_failure_unverified() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        // Establish a known last-known Checked=true first.
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, []));
        ops.QueueAck(true, null);
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule]));
        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "first toggle to settle");
        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, true, false));

        // Second toggle: Get/Put succeed, but the trailing refresh fails.
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [PauseRule]));
        ops.QueueAck(true, null);
        ops.QueueGetFailure("unexpected_reply");

        controller.RequestToggle(false);
        await WaitUntilAsync(() => states.Count >= 5, what: "second toggle to settle");

        await Assert.That(states[^1]).IsEqualTo(new PauseState(true, false, false)); // last-known Checked retained
        await Assert.That(notifications).IsEmpty(); // trailing-refresh failure alone never notifies
    }

    [Test]
    [Arguments("daemon_unreachable", "The daemon is not reachable")]
    [Arguments("unexpected_reply", "Couldn't update launch pause: unexpected_reply")]
    public async Task Get_or_put_failure_notifies_and_still_attempts_trailing_refresh(string reason, string expectedNotification) {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGetFailure(reason); // the initial Get itself fails
        ops.QueueGet(new ConsentPolicyDto("prompt", 30, [])); // trailing refresh still runs and succeeds

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(notifications).IsEquivalentTo([expectedNotification], CollectionOrdering.Matching);
        await Assert.That(ops.PutCalls).IsEqualTo(0); // never reached Put — the initial Get failed
        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, true, false)); // trailing refresh decides Verified
    }

    [Test]
    public async Task Disconnect_mid_toggle_banners_and_leaves_unverified() {
        // Spec §12 acceptance pin: "disconnect mid-toggle (→ daemon_unreachable banner +
        // unverified)" — the SAME disconnected daemon fails both the primary op and the
        // trailing refresh that follows it.
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        using var controller = new PauseController(ops, notifications.Add, CancellationToken.None);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        ops.QueueGetFailure("daemon_unreachable");
        ops.QueueGetFailure("daemon_unreachable"); // trailing refresh also fails

        controller.RequestToggle(true);
        await WaitUntilAsync(() => states.Count >= 3, what: "toggle to settle");

        await Assert.That(notifications).IsEquivalentTo(["The daemon is not reachable"], CollectionOrdering.Matching);
        await Assert.That(states[^1]).IsEqualTo(new PauseState(false, false, false)); // unverified, last-known Checked
    }

    // ---- shutdown ----

    [Test]
    public async Task Shutdown_cancellation_quiet() {
        var ops = new ScriptedLocalControlOps();
        var notifications = new List<string>();
        var cts = new CancellationTokenSource();
        using var controller = new PauseController(ops, notifications.Add, cts.Token);
        var states = new List<PauseState>();
        using var sub = controller.State.Subscribe(states.Add);

        var gate = ops.ArmGet(); // toggle's own Get, held
        controller.RequestToggle(true);
        await WaitUntilAsync(() => ops.GetCalls >= 1, what: "toggle Get to be issued");
        await Assert.That(states[^1].Busy).IsTrue();
        var statesBeforeCancel = states.Count;

        cts.Cancel(); // fires the registered callback synchronously, cancelling the held Get

        // Drive the lane back to Idle deterministically: repeatedly (never sleep-and-hope)
        // request a refresh until it is actually ACCEPTED — a request while still busy is a
        // silent drop, so GetCalls advancing past the toggle's own call IS the "lane free" proof.
        // The token stays cancelled, so this fresh attempt immediately OCEs too, same as real
        // LocalControlOps (spec §10) — that's fine, it only needs to prove acceptance.
        await WaitUntilAsync(() => { controller.RequestRefresh(); return ops.GetCalls >= 2; },
            what: "lane to free after shutdown cancellation");

        await Assert.That(notifications).IsEmpty();
        await Assert.That(states.Count).IsEqualTo(statesBeforeCancel); // no push from either cancellation
    }
}
