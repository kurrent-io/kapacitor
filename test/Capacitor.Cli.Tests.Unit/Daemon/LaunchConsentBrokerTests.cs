using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentBrokerTests {
    static LaunchConsentPromptRequest Req(string id = "a1") =>
        new(id, "user_x", "agent", "/tmp/repo", "claude", DateTimeOffset.UtcNow.ToString("O"), 5);

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
        var pending = broker.PromptAsync(Req(), TimeSpan.FromSeconds(30), CancellationToken.None);
        var delivered = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(delivered.RequestId).IsEqualTo("a1");
        await Assert.That(broker.TryResolve("a1", allow: true)).IsTrue();
        await Assert.That(await pending).IsEqualTo(true);
        await Assert.That(broker.TryResolve("a1", allow: true)).IsFalse(); // already resolved
        await Assert.That(reader.TryRead(out _)).IsFalse(); // no duplicate item queued
    }

    [Test]
    public async Task Prompt_times_out_to_null() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req(), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Timeout_claims_the_entry_so_a_later_TryResolve_reports_false() {
        // Ok=true on the IPC ack must guarantee the decision applied — so once the timeout has
        // won the race and denied the launch, a resolver arriving after must be told it lost
        // (TryResolve=false), never allowed to silently "apply" to an already-decided launch.
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req("a-timeout"), TimeSpan.FromMilliseconds(100), CancellationToken.None);
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

        var promptA = broker.PromptAsync(Req("dup-id"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Assert.That(broker.TryResolve("dup-id", allow: true)).IsTrue();
        await Assert.That(await promptA).IsEqualTo(true);

        var promptB = broker.PromptAsync(Req("dup-id"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Assert.That(broker.PendingSnapshot().Any(r => r.RequestId == "dup-id")).IsTrue();
        await Assert.That(broker.TryResolve("dup-id", allow: true)).IsTrue();
        await Assert.That(await promptB).IsEqualTo(true);
    }

    [Test]
    public async Task Late_subscriber_receives_pending_snapshot_replay() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe(); // HasSubscriber must be true for the gate to even prompt
        var pending = broker.PromptAsync(Req("a2"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
        var (_, lateReader) = broker.Subscribe();
        var replayed = await lateReader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(replayed.RequestId).IsEqualTo("a2");
        broker.TryResolve("a2", false);
        await Assert.That(await pending).IsEqualTo(false);
    }
}
