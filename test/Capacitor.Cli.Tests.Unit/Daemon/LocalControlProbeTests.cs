using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// End-to-end coverage of <see cref="LocalControlProbe.ProbeAsync"/> over a REAL Unix-domain
/// socket. Mirrors <see cref="LocalControlHelloTests"/>'s harness (temp DaemonLockPaths
/// override, socket-file poll, Windows guard) as a style-copy rather than a shared helper, per
/// that file's own note about not disturbing its structure.
/// </summary>
public class LocalControlProbeTests {
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
            ) => throw new NotSupportedException("LocalControlProbeTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    static async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var stateDir = Directory.CreateTempSubdirectory("kcap-probe-ipc-state-").FullName;
        var store       = new LaunchConsentStore(stateDir, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateDir, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
        var consentIpc  = new LaunchConsentIpc(broker, store, NullLogger<LaunchConsentIpc>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            StateDir     = stateDir,
            WorktreeRoot = Path.Combine(Path.GetTempPath(), "kcap-probe-ipc-wt-" + Guid.NewGuid().ToString("N")[..8]),
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
    /// LocalControlHelloTests's RunAsync. Each [Test] still carries its own
    /// [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")] + Windows guard,
    /// since those must be visible on the test method itself.
    static async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-probe-sock-");
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

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Probe_returns_hello_and_first_snapshot_with_consistent_identity() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("probe-a", async (h, ct) => {
            h.Config.InstanceId = "inst-p1";
            var r = await LocalControlProbe.ProbeAsync("probe-a", TimeSpan.FromSeconds(5), ct);

            await Assert.That(r.Reachable).IsTrue();
            await Assert.That(r.Hello!.DaemonName).IsEqualTo("probe-a");
            await Assert.That(r.Snapshot!.Daemon.InstanceId).IsEqualTo("inst-p1");
            await Assert.That(r.IdentityConsistent).IsTrue();
        });
    }

    [Test]
    public async Task Probe_on_missing_socket_reports_unreachable_without_throwing() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path — see LocalSocketPaths

        var r = await LocalControlProbe.ProbeAsync("no-such-daemon-xyz", TimeSpan.FromMilliseconds(500));
        await Assert.That(r.Reachable).IsFalse();
        await Assert.That(r.Hello).IsNull();
    }
}
