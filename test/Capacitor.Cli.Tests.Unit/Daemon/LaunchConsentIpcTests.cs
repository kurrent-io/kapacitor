using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// End-to-end coverage of the consent frames (ConsentSubscribe/ConsentResolve/
/// ConsentRulesGet/ConsentRulesPut) over a REAL Unix-domain socket — the same
/// LocalControlServer.HandleConnectionAsync routing switch a real `kcap` client talks to.
/// The harness mirrors AgentOrchestratorLocalAttachTests's real-socket tests (temp
/// DaemonLockPaths override, socket-file poll, Windows guard) but builds its own minimal
/// AgentOrchestrator, since none of these tests exercise Spawn/Attach/List/Stop — the
/// orchestrator only needs to exist to satisfy LocalControlServer's constructor.
/// </summary>
public class LaunchConsentIpcTests {
    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40
            ) => throw new NotSupportedException("LaunchConsentIpcTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(
        LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection,
        LaunchConsentStore Store, LaunchConsentBroker Broker, LaunchConsentGate Gate, string SockPath);

    static async Task<Harness> StartAsync(
            string daemonName, LaunchConsentDefault def, int promptTimeoutSeconds, CancellationToken ct
        ) {
        var stateDir = Directory.CreateTempSubdirectory("kcap-consent-ipc-state-").FullName;
        var store = new LaunchConsentStore(stateDir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(def, promptTimeoutSeconds, []), out _);
        var broker = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateDir, NullLogger.Instance);
        var gate = new LaunchConsentGate(store, decisionLog, broker, NullLogger<LaunchConsentGate>.Instance);
        var consentIpc = new LaunchConsentIpc(broker, store, NullLogger<LaunchConsentIpc>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            StateDir     = stateDir,
            WorktreeRoot = Path.Combine(Path.GetTempPath(), "kcap-consent-ipc-wt-" + Guid.NewGuid().ToString("N")[..8]),
        };

        var connection       = new ServerConnection(config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, connection, worktreeManager, repoMatcher, new NoopPtyProcessFactory(), new NoopHttpClientFactory(),
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate);

        var restart = RestartCoordinator.ForTest(daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = LocalSocketPaths.Socket(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(server, orchestrator, connection, store, broker, gate, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
    }

    /// Wraps a test body with the temp-dir DaemonLockPaths override + harness lifecycle, mirroring
    /// AgentOrchestratorLocalAttachTests's real-socket tests. Each [Test] still carries its own
    /// [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")] + Windows guard,
    /// since those must be visible on the test method itself.
    static async Task RunAsync(
            string daemonName, LaunchConsentDefault def, int promptTimeoutSeconds,
            Func<Harness, CancellationToken, Task> body
        ) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-consent-sock-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, def, promptTimeoutSeconds, cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    /// Waits for the daemon's accept loop to actually process a just-sent ConsentSubscribe frame
    /// (i.e. for LaunchConsentIpc.HandleSubscribeAsync to call broker.Subscribe()) — a bounded poll
    /// bridging the gap between "frame written to the socket" and "server-side subscription live".
    static async Task WaitForSubscriberAsync(LaunchConsentBroker broker, CancellationToken ct) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!broker.HasSubscriber && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task RulesGet_returns_current_policy_and_RulesPut_replaces_it() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("test-consent-rules", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            // Act 1: RulesGet reports the current (default, empty) policy.
            await using (var s1 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s1, new LocalFrame(FrameType.ConsentRulesGet), ct);
                var resp = await FrameCodec.ReadAsync(s1, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentRules);
                var dto = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentPolicyDto);
                await Assert.That(dto!.Default).IsEqualTo("allow");
                await Assert.That(dto.Rules.Count).IsEqualTo(0);
            }

            // Act 2: RulesPut replaces the policy; the store reflects the new default.
            await using (var s2 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s2, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                    """{"default":"deny","prompt_timeout_seconds":30,"rules":[]}"""), ct);
                var resp = await FrameCodec.ReadAsync(s2, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(h.Store.Current.Default).IsEqualTo(LaunchConsentDefault.Deny);
            }

            // Act 3: an invalid rule action is rejected with an explanatory error.
            await using (var s3 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s3, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                    """{"default":"allow","prompt_timeout_seconds":45,"rules":[{"action":"bogus","requester":null,"kind":null,"repo":null,"vendor":null}]}"""), ct);
                var resp = await FrameCodec.ReadAsync(s3, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsFalse();
                await Assert.That(ack.Error).Contains("action");
            }
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Subscribe_receives_pending_and_Resolve_unblocks_the_gate() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("test-consent-subscribe", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            // Subscribe FIRST so the gate's HasSubscriber check (evaluated synchronously before it
            // ever awaits the prompt) sees a live subscriber — otherwise DecideAsync would
            // short-circuit to "prompt_no_ui". Writing the frame only queues it; the daemon-side
            // broker.Subscribe() call (what actually flips HasSubscriber) happens asynchronously
            // once the accept loop reads it off the socket, so wait for that to land for real
            // before starting the background decide — a bare write-then-go race intermittently
            // hung this test (DecideAsync denying with prompt_no_ui before ever touching the broker).
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await WaitForSubscriberAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude");
            var decideTask = h.Gate.DecideAsync("a9", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);
            var pendingDto = JsonSerializer.Deserialize(pending.Text, ConsentIpcJsonContext.Default.ConsentPendingDto);
            await Assert.That(pendingDto!.RequestId).IsEqualTo("a9");

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a9","decision":"allow","save_rule":null}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                await Assert.That(ackFrame!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(ackFrame.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
            }

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsTrue();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_with_save_rule_appends_to_policy() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("test-consent-saverule", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await WaitForSubscriberAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "review-flow", "/tmp/repo", "claude");
            var decideTask = h.Gate.DecideAsync("a10", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a10","decision":"deny","save_rule":{"action":"deny","kind":"review-flow","requester":null,"repo":null,"vendor":null}}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                var ack = JsonSerializer.Deserialize(ackFrame!.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(ack.Error).IsNull(); // the save succeeded — no partial-failure warning to report
            }

            await Assert.That(h.Store.Current.Rules
                .Any(r => r.Action == "deny" && r.Kind == "review-flow")).IsTrue();

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsFalse();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_unknown_request_acks_false() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("test-consent-unknown", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                """{"request_id":"nope","decision":"allow","save_rule":null}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
        });
    }

    // ══ Code-review follow-up: STJ source-gen does NOT enforce non-nullable members — a
    // syntactically valid payload missing a required field deserializes with that field left
    // null rather than throwing JsonException. Before the fix, both handlers reached code that
    // dereferenced/used the null value directly (dto.Rules.Select(...), broker.TryResolve(null,
    // ...)), throwing an UNCAUGHT exception (only JsonException was caught) that dropped the
    // connection with no ConsentAck reply at all. These tests pin the fixed behavior: a
    // malformed-but-parseable payload always gets a ConsentAck(false, ...) reply, never a
    // dropped connection. ══════════════════════════════════════════════════════════════════

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task RulesPut_missing_rules_field_acks_false_without_dropping_the_connection() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("put-norules", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // No "rules" key at all.
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                """{"default":"allow","prompt_timeout_seconds":45}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
            await Assert.That(ack.Error).Contains("malformed");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task RulesPut_null_rules_element_acks_false_without_dropping_the_connection() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        // "rules":[null] is valid JSON — STJ source-gen deserializes it into a List<ConsentRuleDto>
        // containing a null element despite the non-nullable C# declaration. Any(r => r.Action is
        // null) would throw an uncaught NullReferenceException on that element (only JsonException
        // is caught), dropping the connection with no ConsentAck at all. Pins the fixed guard
        // (r is null || r.Action is null).
        await RunAsync("put-nullrule", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                """{"default":"allow","prompt_timeout_seconds":45,"rules":[null]}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
            await Assert.That(ack.Error).Contains("malformed");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_missing_request_id_acks_false_without_dropping_the_connection() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("resolve-noid", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // No "request_id" key at all.
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                """{"decision":"allow","save_rule":null}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
        });
    }

    // ══ Code-review follow-up: ack conflation fix. Ok now reflects the RESOLUTION outcome only;
    // a rejected save_rule is a secondary, partial failure that rides along as Error even when
    // Ok=true, rather than being indistinguishable from "no pending request with that id". ══════

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_with_an_invalid_save_rule_still_resolves_but_reports_the_save_error() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("saverule-bad", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await WaitForSubscriberAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude");
            var decideTask = h.Gate.DecideAsync("a11", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                // The save_rule's action is invalid — the store rejects it — but the resolution
                // itself (the owner's "allow" decision) must still apply.
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a11","decision":"allow","save_rule":{"action":"bogus","requester":null,"kind":null,"repo":null,"vendor":null}}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                var ack = JsonSerializer.Deserialize(ackFrame!.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                // Ok=true: the resolution applied. Error carries the save's partial failure —
                // NOT the same shape as the "unknown id" (Ok=false) case asserted above.
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(ack.Error).Contains("action");
            }

            // The invalid rule was never persisted.
            await Assert.That(h.Store.Current.Rules.Count).IsEqualTo(0);

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsTrue();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }
}
