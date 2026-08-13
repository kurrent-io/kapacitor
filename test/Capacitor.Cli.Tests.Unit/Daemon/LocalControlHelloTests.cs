using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// End-to-end coverage of the Hello/HelloReply frame pair over a REAL Unix-domain socket —
/// the same <see cref="LocalControlServer.HandleConnectionAsync"/> routing switch a real
/// `kcap` client talks to. The harness mirrors <c>LaunchConsentIpcTests</c> (temp
/// DaemonLockPaths override, socket-file poll, Windows guard) but builds its own minimal
/// AgentOrchestrator, since none of these tests exercise Spawn/Attach/Stop — the
/// orchestrator (and the consent plumbing) only need to exist to satisfy
/// LocalControlServer's constructor.
/// </summary>
public class LocalControlHelloTests {
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
            ) => throw new NotSupportedException("LocalControlHelloTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    static async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var stateDir = Directory.CreateTempSubdirectory("kcap-hello-ipc-state-").FullName;
        var store       = new LaunchConsentStore(stateDir, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateDir, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
        var consentIpc  = new LaunchConsentIpc(broker, store, NullLogger<LaunchConsentIpc>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            StateDir     = stateDir,
            WorktreeRoot = Path.Combine(Path.GetTempPath(), "kcap-hello-ipc-wt-" + Guid.NewGuid().ToString("N")[..8]),
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

        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, new DaemonStatusNotifier());
        var restart = RestartCoordinator.ForTest(daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = LocalSocketPaths.Socket(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(server, orchestrator, connection, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
    }

    /// Wraps a test body with the temp-dir DaemonLockPaths override + harness lifecycle, mirroring
    /// LaunchConsentIpcTests's RunAsync. Each [Test] still carries its own
    /// [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")] + Windows guard,
    /// since those must be visible on the test method itself.
    static async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-hello-sock-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token);
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

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Hello_with_client_info_gets_a_reply_naming_version_name_and_capabilities() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-a", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            var clientHello = JsonSerializer.Serialize(
                new ClientHelloDto("kcap-cli", "1.2.3"), HelloIpcJsonContext.Default.ClientHelloDto);
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, clientHello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "status/1" });
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Hello_with_empty_payload_gets_an_identical_reply() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-b", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "status/1" });
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Hello_with_malformed_json_payload_is_treated_as_empty_and_still_replies() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-c", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // Payload is diagnostics-only — malformed JSON must not drop the connection or
            // change the reply in any way.
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, "{not json"), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull();
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "status/1" });
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Hello_reply_carries_pid_and_instance_id() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-id", async (h, ct) => {
            h.Config.InstanceId = "inst-test-1";
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var frame = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto);

            await Assert.That(dto!.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(dto.InstanceId).IsEqualTo("inst-test-1");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task List_still_returns_AgentList_alongside_the_new_Hello_route() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-d", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.List), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Unrouted_frame_type_gets_an_error_reply_mentioning_hello() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("hello-e", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // Detach is a valid, decodable FrameType that LocalControlServer's switch doesn't
            // route anywhere — it falls into the default arm, which is what this pins: the Error
            // reply for a decodable-but-unrouted frame. It is NOT the down-level discovery signal —
            // that is hello-then-EOF (a pre-hello daemon can't even decode byte 15), never an Error
            // frame (§3.1 of the design doc).
            await FrameCodec.WriteAsync(s, LocalFrame.Detach(), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(resp.Text).Contains("Hello");
        });
    }
}
