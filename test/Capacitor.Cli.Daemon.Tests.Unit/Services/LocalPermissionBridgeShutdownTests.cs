using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Shutdown through the real StopAsync: a claimed answer is delivered inside the drain, an
/// admitted-but-unstarted handler is still counted, and a context arriving after admission
/// closed is rejected without ever being tracked. The decision log is wired like production so
/// the shutdown-claim rule is observable: a request the shutdown claim WINS writes no record, and
/// a request it LOSES logs whichever claim actually won.
public class LocalPermissionBridgeShutdownTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    static (LocalPermissionBridge bridge, FakeServerConnection server, PermissionPromptBroker broker, TempDir tmp) Build() {
        var server = new FakeServerConnection(respond: null);
        var broker = new PermissionPromptBroker();
        var tmp    = new TempDir();
        var log    = new PermissionDecisionLog(tmp.Path, NullLogger.Instance);
        var bridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance, broker, log) {
            AttributeHandler = _ => new AttributedAgent("agent-1"),
        };
        server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        return (bridge, server, broker, tmp);
    }

    static string[] LogLines(TempDir tmp) {
        var path = tmp.PathTo("permission-decisions.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    static Task<HttpResponseMessage> Post(HttpClient client, LocalPermissionBridge bridge) =>
        client.PostAsync($"{bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", agent_id = "agent-1" }));

    static async Task<string> BehaviorOf(HttpResponseMessage r) {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
    }

    static async Task WaitUntil(Func<bool> c, string what) {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!c()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(what); await Task.Delay(10); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Shutdown_with_no_other_claim_answers_deny_with_no_record_and_the_leg_completes() {
        var (bridge, _, broker, tmp) = Build();
        try {
            await bridge.StartAsync(CancellationToken.None);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = Post(client, bridge);
            await WaitUntil(() => broker.PendingSnapshot().Count == 1, "pending");

            var stop = bridge.StopAsync(CancellationToken.None);
            await Assert.That(await BehaviorOf(await response)).IsEqualTo("deny");
            await stop;
            await Assert.That(bridge.ServerLegsInFlightForTest).IsEqualTo(0);
            await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(0);
            await Assert.That(LogLines(tmp)).IsEmpty().Because("the shutdown claim won — no other party settled, so nothing is recorded");
            await bridge.DisposeAsync();
        } finally {
            tmp.Dispose();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task App_claim_landing_as_the_token_fires_is_delivered_inside_the_drain() {
        var (bridge, _, broker, tmp) = Build();
        try {
            await bridge.StartAsync(CancellationToken.None);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = Post(client, bridge);
            await WaitUntil(() => broker.PendingSnapshot().Count == 1, "pending");
            var requestId = broker.PendingSnapshot()[0].RequestId;

            // Claim first, then stop: the token fires after the claim; the bridge's shutdown claim must lose.
            await Assert.That(broker.TrySettle(requestId, new PermissionDecision("allow", null, null), "allow", "app")).IsTrue();
            var stop = bridge.StopAsync(CancellationToken.None);
            await Assert.That(await BehaviorOf(await response)).IsEqualTo("allow");
            await stop;
            await Assert.That(LogLines(tmp).Length).IsEqualTo(1);
            await Assert.That(LogLines(tmp)[0]).Contains("\"source\":\"app\"");
            await bridge.DisposeAsync();
        } finally {
            tmp.Dispose();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Admitted_handler_held_before_entry_is_drained_with_the_token_already_cancelled() {
        var (bridge, _, broker, tmp) = Build();
        try {
            var hold = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            bridge.BeforeHandlerRunsForTest = async () => { entered.TrySetResult(); await hold.Task; };
            await bridge.StartAsync(CancellationToken.None);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = Post(client, bridge);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(1);

            var started = DateTime.UtcNow;
            var stop = bridge.StopAsync(CancellationToken.None);
            hold.SetResult();                       // the delegate runs despite the cancelled token
            await Assert.That(await BehaviorOf(await response)).IsEqualTo("deny");
            await stop;
            await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(0);
            await Assert.That(DateTime.UtcNow - started < LocalPermissionBridge.ShutdownDrain).IsTrue().Because("the drain must not expire");
            await bridge.DisposeAsync();
        } finally {
            tmp.Dispose();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Context_arriving_after_admission_closed_is_rejected_untracked() {
        var (bridge, _, _, tmp) = Build();
        try {
            var hold = new TaskCompletionSource();
            bridge.BeforeHandlerRunsForTest = () => hold.Task;
            await bridge.StartAsync(CancellationToken.None);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var first = Post(client, bridge);
            await WaitUntil(() => bridge.InFlightHandlersForTest == 1, "first admitted");

            var stop = bridge.StopAsync(CancellationToken.None);   // closes admission, drains the first
            await WaitUntil(() => !bridge.AdmittingForTest, "admission closed");
            var second = await Post(client, bridge);
            await Assert.That((int)second.StatusCode).IsEqualTo(503);
            await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(1);
            hold.SetResult();
            await first;
            await stop;
            await bridge.DisposeAsync();
        } finally {
            tmp.Dispose();
        }
    }
}
