using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Covers <see cref="PendingPermissionRegistry"/> — the daemon-side correlation between a
/// hosted-agent permission request (the daemon gets a requestId back from RequestPermission2)
/// and the server's later "PermissionResolved" push carrying the user's decision. Replaces the
/// old model where the decision was the return value of a hub invocation held open for the whole
/// wait (which starved DaemonPing). The registry must:
///   - complete the awaiting call when the matching push arrives,
///   - handle a push that races ahead of the await (buffered, returned on registration),
///   - propagate cancellation (daemon shutdown) and not leak the pending entry,
///   - never deliver one request's decision to another's waiter.
/// </summary>
public class PendingPermissionRegistryTests {
    static PermissionDecision Allow => new("allow", null, null);
    static PermissionDecision Deny  => new("deny", null, null);

    [Test]
    public async Task Resolve_completes_the_awaiting_request() {
        var registry = new PendingPermissionRegistry();

        var pending = registry.AwaitDecisionAsync("req-1", CancellationToken.None);
        await Assert.That(pending.IsCompleted).IsFalse();

        registry.Resolve("req-1", Allow);

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(decision.Behavior).IsEqualTo("allow");
    }

    [Test]
    public async Task Decision_that_arrives_before_the_await_is_buffered_and_returned() {
        var registry = new PendingPermissionRegistry();

        // The push can arrive between RequestPermission2 returning the requestId and the caller
        // registering its await — that decision must not be lost.
        registry.Resolve("req-2", Allow);

        var decision = await registry.AwaitDecisionAsync("req-2", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(decision.Behavior).IsEqualTo("allow");
    }

    [Test]
    public async Task Cancellation_propagates_and_a_late_push_is_a_no_op() {
        var registry = new PendingPermissionRegistry();
        using var cts = new CancellationTokenSource();

        var pending = registry.AwaitDecisionAsync("req-3", cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();

        // A push that lands after cancellation must not throw (the waiter is already gone).
        registry.Resolve("req-3", Allow);
    }

    [Test]
    public async Task Early_buffer_evicts_oldest_when_flooded_with_unconsumed_decisions() {
        var registry = new PendingPermissionRegistry();

        // Push decisions for ids no one ever awaits (duplicate/late/unknown pushes). Without a
        // bound these would accumulate forever (the qodo finding); the buffer must cap and evict
        // oldest-first.
        const int flood = 4000;

        for (var i = 0; i < flood; i++)
            registry.Resolve($"orphan-{i}", Allow);

        // The oldest was evicted → awaiting it does NOT complete from the buffer.
        var oldest = registry.AwaitDecisionAsync("orphan-0", CancellationToken.None);
        await Assert.That(oldest.IsCompleted).IsFalse();

        // The newest is still buffered → awaiting it completes immediately.
        var newest = await registry.AwaitDecisionAsync($"orphan-{flood - 1}", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(newest.Behavior).IsEqualTo("allow");
    }

    [Test]
    public async Task Resolve_for_a_different_request_does_not_complete_the_wrong_waiter() {
        var registry = new PendingPermissionRegistry();

        var pending = registry.AwaitDecisionAsync("req-A", CancellationToken.None);

        registry.Resolve("req-B", Allow);
        await Assert.That(pending.IsCompleted).IsFalse();

        registry.Resolve("req-A", Deny);

        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(decision.Behavior).IsEqualTo("deny");
    }
}
