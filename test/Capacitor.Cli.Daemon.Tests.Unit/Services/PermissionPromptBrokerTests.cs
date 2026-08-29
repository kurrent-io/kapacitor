using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionPromptBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static PermissionPendingDto Dto(string id = "r1", string agent = "a1") =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, DateTimeOffset.UtcNow.ToString("O"));

    static PermissionDecision Allow => new("allow", null, null);

    static async Task<T> WaitBounded<T>(Task<T> task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        return await task;
    }

    [Test]
    public async Task Register_broadcasts_pending_and_settle_broadcasts_resolved_and_completes_the_task() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var settlement = broker.Register(Dto());

        var first = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(((PermissionStreamItem.Pending)first).Dto.RequestId).IsEqualTo("r1");

        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var second = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var resolved = ((PermissionStreamItem.Resolved)second).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("allow");
        await Assert.That(resolved.Source).IsEqualTo("app");

        var s = await WaitBounded(settlement, "the claim completes the registration");
        await Assert.That(s.Decision.Behavior).IsEqualTo("allow");
        await Assert.That(s.Source).IsEqualTo("app");
    }

    [Test]
    public async Task Second_claim_loses_and_the_task_carries_the_first() {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto());
        await Assert.That(broker.TrySettle("r1", new("deny", null, null), "deny", "server")).IsTrue();
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsFalse();
        var s = await WaitBounded(settlement, "first claim");
        await Assert.That(s.Source).IsEqualTo("server");
        await Assert.That(s.Decision.Behavior).IsEqualTo("deny");
    }

    [Test]
    public async Task Subscribe_replays_each_pending_exactly_once() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1"));
        _ = broker.Register(Dto("r2"));
        var (_, reader) = broker.Subscribe();
        var a = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var b = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(new[] { ((PermissionStreamItem.Pending)a).Dto.RequestId, ((PermissionStreamItem.Pending)b).Dto.RequestId })
            .IsEquivalentTo(new[] { "r1", "r2" });
        await Assert.That(reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Withdraw_settles_the_agents_entries_and_a_later_register_for_it_settles_at_once_without_broadcast() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var s1 = broker.Register(Dto("r1", "a1"));
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the Pending

        broker.WithdrawForAgent("a1");
        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("withdrawn");
        await Assert.That(resolved.Source).IsEqualTo("agent_gone");
        await Assert.That((await WaitBounded(s1, "withdrawn")).Decision.Behavior).IsEqualTo("deny");

        var s2 = broker.Register(Dto("r2", "a1"));
        await Assert.That(s2.IsCompletedSuccessfully).IsTrue();
        await Assert.That(s2.Result.Source).IsEqualTo("agent_gone");
        await Assert.That(reader.TryRead(out _)).IsFalse(); // nothing broadcast for r2
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Settle_if_no_subscriber_is_refused_while_a_subscriber_is_registered() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto());
        var (id, _) = broker.Subscribe();
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsFalse();
        broker.Unsubscribe(id);
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsTrue();
    }

    /// The gate invariant: a subscriber that dials during a settlement sees either nothing or
    /// Pending then Resolved — never Pending alone. Driven from many interleavings.
    [Test]
    public async Task Subscribe_racing_settle_never_yields_pending_alone() {
        for (var round = 0; round < 200; round++) {
            var broker = new PermissionPromptBroker();
            _ = broker.Register(Dto());
            var subscribe = Task.Run(() => broker.Subscribe());
            var settle    = Task.Run(() => broker.TrySettle("r1", Allow, "allow", "app"));
            var (id, reader) = await subscribe;
            await settle;
            broker.Unsubscribe(id);

            var items = new List<PermissionStreamItem>();
            while (reader.TryRead(out var item)) items.Add(item);
            var pendings  = items.Count(i => i is PermissionStreamItem.Pending);
            var resolveds = items.Count(i => i is PermissionStreamItem.Resolved);
            await Assert.That(pendings == 0 || resolveds == 1).IsTrue().Because($"round {round}: {pendings} pending, {resolveds} resolved");
        }
    }

    [Test]
    public async Task Unsubscribe_completes_the_channel() {
        var broker = new PermissionPromptBroker();
        var (id, reader) = broker.Subscribe();
        broker.Unsubscribe(id);
        await reader.Completion.WaitAsync(Bounded);
        await Assert.That(broker.HasSubscriber).IsFalse();
    }
}
