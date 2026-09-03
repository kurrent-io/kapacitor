using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The shared-token branch with an attributed agent: the broker is the one claim point, the
/// server leg feeds it, and the hook receives whichever settlement won.
public class LocalPermissionBridgeInteractiveTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public FakeServerConnection Server { get; } = new(respond: null);
        public PermissionPromptBroker Broker { get; } = new();
        public TempDir Tmp { get; } = new();
        public PermissionDecisionLog Log { get; }
        public LocalPermissionBridge Bridge { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

        public Harness(string? attributeTo = "agent-1") {
            Log    = new PermissionDecisionLog(Tmp.Path, NullLogger.Instance);
            Bridge = new LocalPermissionBridge(Server, NullLogger<LocalPermissionBridge>.Instance, Broker, Log) {
                AttributeHandler = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
            };
        }

        public async Task StartAsync() => await Bridge.StartAsync(CancellationToken.None);

        /// Posts a Claude hook payload; the returned task completes when the hook is answered.
        public Task<HttpResponseMessage> PostAsync(string toolName = "Bash", string? agentId = "agent-1") =>
            Client.PostAsync($"{Bridge.BaseUrl}/claude/permission-request",
                JsonContent.Create(new { session_id = Session, tool_name = toolName, tool_input = new { command = "ls" }, agent_id = agentId, cwd = "/repo" }));

        public static async Task<string> BehaviorOf(HttpResponseMessage response) {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
        }

        public string[] LogLines() {
            var path = Tmp.PathTo("permission-decisions.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }

        public async Task<PermissionPendingDto> WaitPendingAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Broker.PendingSnapshot().Count == 0) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for a pending request");
                await Task.Delay(10);
            }
            return Broker.PendingSnapshot().Single();
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); Tmp.Dispose(); }
    }

    static PermissionDecision Allow => new("allow", null, null);
    static PermissionDecision Deny  => new("deny", null, null);

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task App_claim_first_answers_the_hook_cancels_the_server_await_responds_to_the_server_and_logs_app() {
        await using var h = new Harness();
        var awaitCts = new TaskCompletionSource<CancellationToken>();
        h.Server.AwaitScript = (_, ct) => { awaitCts.SetResult(ct); return new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct); };
        await h.StartAsync();

        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.SessionId).IsEqualTo(Session);
        await Assert.That(pending.AgentId).IsEqualTo("agent-1");

        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsTrue();
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");

        var ct = await awaitCts.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntil(() => ct.IsCancellationRequested, "the server await is cancelled");
        await WaitUntil(() => h.Server.Responds.Count == 1, "RespondToPermission is invoked");
        await Assert.That(h.Server.Responds[0].RequestId).IsEqualTo("srv-1");
        await Assert.That(h.Server.Responds[0].Decision.Behavior).IsEqualTo("allow");

        var lines = h.LogLines();
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"source\":\"app\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task The_hook_bodys_tool_use_id_rides_the_pending_dto() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        await h.StartAsync();

        var response = h.Client.PostAsync($"{h.Bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", tool_input = new { command = "ls" }, tool_use_id = "toolu_01X", agent_id = "agent-1", cwd = "/repo" }));
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.ToolUseId).IsEqualTo("toolu_01X");

        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsTrue();
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Server_claim_first_answers_the_hook_pushes_resolved_server_and_a_later_app_claim_loses() {
        await using var h = new Harness();
        var serverDecision = new TaskCompletionSource<PermissionDecision>();
        h.Server.AwaitScript = (_, ct) => serverDecision.Task.WaitAsync(ct);
        await h.StartAsync();
        var (_, reader) = h.Broker.Subscribe();

        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // Pending

        serverDecision.SetResult(Deny);
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("deny");
        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.Source).IsEqualTo("server");
        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsFalse();
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"server\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Respond_reporting_not_pending_is_logged_not_treated_as_a_conflict() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        h.Server.RespondScript = () => new ServerConnection.RespondOutcome(ServerConnection.RespondOutcomeKind.NotPending, "Permission request is no longer pending.");
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        await WaitUntil(() => h.Server.Responds.Count == 1, "respond attempted");
        await Assert.That(h.LogLines()[0]).Contains("\"outcome\":\"allow\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_fault_with_no_subscriber_denies_as_no_ui_and_logs_it() {
        await using var h = new Harness();
        h.Server.BeginScript = (_, _) => throw new Microsoft.AspNetCore.SignalR.HubException("boom");
        await h.StartAsync();
        var response = await h.PostAsync();
        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("deny");
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"no_ui\"");
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_fault_with_a_subscriber_keeps_the_request_answerable() {
        await using var h = new Harness();
        var (id, _) = h.Broker.Subscribe();
        h.Server.BeginScript = (_, _) => throw new Microsoft.AspNetCore.SignalR.HubException("boom");
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await Task.Delay(100);
        await Assert.That(response.IsCompleted).IsFalse();
        h.Broker.Unsubscribe(id);
        await Task.Delay(100);
        await Assert.That(response.IsCompleted).IsFalse().Because("a subscriber leaving never denies");
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_held_in_readiness_wait_is_abandoned_by_the_predicate_with_the_cancellation_held() {
        await using var h = new Harness();
        var release = new TaskCompletionSource();
        Func<bool>? seen = null;
        var invoked = 0;
        // Models ConnectionRetry: wait for "readiness" (the release), then check the predicate
        // immediately before the invoke. The token is deliberately IGNORED so the queued
        // cancellation cannot be what ends the leg.
        h.Server.BeginScript = async (_, abandoned) => {
            seen = abandoned;
            await release.Task;
            if (abandoned()) throw new PermissionRequestAbandonedException();
            invoked++;
            return "srv-1";
        };
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await WaitUntil(() => seen is not null, "Begin entered");

        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        release.SetResult();
        await WaitUntil(() => h.Bridge.ServerLegsInFlightForTest == 0, "the leg completes");
        await Assert.That(invoked).IsEqualTo(0);
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Withdrawal_during_a_held_begin_settles_withdrawn_and_the_leg_completes() {
        await using var h = new Harness();
        var release = new TaskCompletionSource();
        h.Server.BeginScript = async (ct, abandoned) => { await release.Task.WaitAsync(ct); if (abandoned()) throw new PermissionRequestAbandonedException(); return "srv-1"; };
        await h.StartAsync();
        var response = h.PostAsync();
        _ = await h.WaitPendingAsync();
        h.Broker.WithdrawForAgent("agent-1");
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("deny");
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"agent_gone\"");
        await WaitUntil(() => h.Bridge.ServerLegsInFlightForTest == 0, "the leg completes");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Unattributed_request_takes_the_server_only_path() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();
        var response = await h.PostAsync();
        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("allow"); // the unscripted fake answers allow
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(h.LogLines().Length).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Oversized_tool_input_is_omitted_on_the_wire_with_the_flag() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        await h.StartAsync();
        var big = new string('x', PermissionWire.MaxElementBytes);
        var response = h.Client.PostAsync($"{h.Bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", tool_input = new { command = big }, agent_id = "agent-1" }));
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.ToolInput).IsNull();
        await Assert.That(pending.ToolInputOmitted).IsTrue();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }

    [Test]
    public async Task Build_pending_bounds() {
        var ok = LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t");
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.ToolName).IsEqualTo("Bash");
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", new string('n', PermissionWire.MaxToolNameBytes + 1), null, null, "t")).IsNull();
        await Assert.That(LocalPermissionBridge.BuildPending("r", new string('k', PermissionWire.MaxAgentIdBytes + 1), Session, "claude", "Bash", null, null, "t")).IsNull();
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "codex", null, null, null, "t")!.ToolName).IsEqualTo("");
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t", "toolu_1")!.ToolUseId).IsEqualTo("toolu_1");
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t", new string('i', PermissionWire.MaxToolUseIdBytes + 1))!.ToolUseId).IsNull();
    }

    static async Task WaitUntil(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
