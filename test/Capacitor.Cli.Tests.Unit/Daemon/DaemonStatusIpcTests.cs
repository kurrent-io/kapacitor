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
/// End-to-end coverage of the StatusSubscribe/DaemonStatus frame pair over a REAL Unix-domain
/// socket — the same <see cref="LocalControlServer.HandleConnectionAsync"/> routing switch a real
/// `kcap` client talks to. The harness mirrors <see cref="LocalControlHelloTests"/> (temp
/// DaemonLockPaths override, socket-file poll, Windows guard) and is reused verbatim by the
/// follow-up task that exercises the debounce/pulse matrix — hence the harness exposing
/// <c>Notifier</c>/<c>StatusIpc</c> on <see cref="Harness"/>.
/// </summary>
public class DaemonStatusIpcTests {
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
            ) => throw new NotSupportedException("DaemonStatusIpcTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(
        LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection,
        DaemonConfig Config, string SockPath, DaemonStatusNotifier Notifier, DaemonStatusIpc StatusIpc);

    static async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var stateDir = Directory.CreateTempSubdirectory("kcap-status-ipc-state-").FullName;
        var store       = new LaunchConsentStore(stateDir, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateDir, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
        var consentIpc  = new LaunchConsentIpc(broker, store, NullLogger<LaunchConsentIpc>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            StateDir     = stateDir,
            WorktreeRoot = Path.Combine(Path.GetTempPath(), "kcap-status-ipc-wt-" + Guid.NewGuid().ToString("N")[..8]),
        };

        var notifier   = new DaemonStatusNotifier();
        var connection = new ServerConnection(
            config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, notifier);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, connection, worktreeManager, repoMatcher, new NoopPtyProcessFactory(), new NoopHttpClientFactory(),
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, notifier) {
            Debounce = TimeSpan.FromMilliseconds(25), // fast tests; 250ms is the production default
        };

        var restart = RestartCoordinator.ForTest(daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = LocalSocketPaths.Socket(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(server, orchestrator, connection, config, sockPath, notifier, statusIpc);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
    }

    /// Wraps a test body with the temp-dir DaemonLockPaths override + harness lifecycle, mirroring
    /// LocalControlHelloTests's RunAsync. Each [Test] still carries its own
    /// [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")] + Windows guard,
    /// since those must be visible on the test method itself.
    static async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-status-sock-");
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

    static async Task<DaemonStatusDto> ReadStatusAsync(Stream s, CancellationToken ct) {
        var f = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(f!.Type).IsEqualTo(FrameType.DaemonStatus);
        return JsonSerializer.Deserialize(f.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Subscribe_pushes_an_immediate_snapshot_with_daemon_block_and_agents() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("st-a", async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1", kind: LaunchKind.ReviewFlow,
                flowRunId: "flow_1", flowRole: "reviewer", requester: "github:12345");
            h.Orchestrator.SeedAgentForTest("s2", status: "Starting");

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            var dto = await ReadStatusAsync(s, ct);
            await Assert.That(dto.Daemon.Name).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Daemon.Version).IsNotEmpty();
            await Assert.That(dto.Daemon.ServerUrl).IsEqualTo(h.Config.ServerUrl);
            await Assert.That(dto.Daemon.Connection).IsEqualTo("disconnected"); // no live hub in tests
            await Assert.That(dto.Daemon.MaxAgents).IsEqualTo(h.Config.MaxConcurrentAgents);
            await Assert.That(dto.Daemon.ActiveAgents).IsEqualTo(2); // Running + Starting
            await Assert.That(dto.Agents.Count).IsEqualTo(2);
            var r1 = dto.Agents.Single(a => a.Id == "s1");
            await Assert.That(r1.Kind).IsEqualTo("review-flow");
            await Assert.That(r1.Requester).IsEqualTo("github:12345");
        });
    }
}
