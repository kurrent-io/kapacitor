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

    // ---- review fix: a reachable peer answering well-formed-but-structurally-degenerate JSON ----

    delegate Task ConnScript(NetworkStream s, CancellationToken ct);

    /// Minimal scripted-connection UDS listener — a trimmed copy of
    /// <c>LocalControlClientTests.ScriptedServer</c> (that one is a private nested type there,
    /// so it can't be referenced directly), sized for exactly what this file needs: one script
    /// per accepted connection, in order.
    sealed class ScriptedServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        int _served;
        readonly Task _accept;

        public ScriptedServer(string sockPath, params ConnScript[] scripts) {
            _scripts = scripts;
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        var idx = Interlocked.Increment(ref _served) - 1;
                        if (idx >= _scripts.Length) { conn.Dispose(); continue; }
                        var script = _scripts[idx];
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            try { await _accept; } catch { }
        }
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Probe_treats_a_structurally_degenerate_snapshot_as_a_snapshot_failure_not_a_throw() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        var sockDir = Directory.CreateTempSubdirectory("kcap-probe-degenerate-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        try {
            var name = "probe-degenerate";
            var helloJson = JsonSerializer.Serialize(
                new HelloReplyDto(1, "1.0", name, ["status/1"], 111, "inst-x"),
                HelloIpcJsonContext.Default.HelloReplyDto);

            ConnScript helloThen = async (s, ct) => {
                var f = await FrameCodec.ReadAsync(s, ct);
                if (f?.Type == FrameType.Hello)
                    await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, helloJson), ct);
            };
            // Well-formed JSON, but structurally degenerate: daemon/agents both null. STJ source-gen
            // leaves declared-non-nullable reference members at their default on null/absent JSON
            // rather than throwing, so this deserializes to a NON-null DaemonStatusDto with a null
            // Daemon — exactly the shape that must go through DaemonStatusValidator.IsValid instead
            // of a bare null-check, or ProbeAsync either NREs on snapshot.Daemon.Pid or silently
            // returns a null-riddled Snapshot.
            ConnScript subscribeDegenerate = async (s, ct) => {
                var f = await FrameCodec.ReadAsync(s, ct);
                if (f?.Type == FrameType.StatusSubscribe)
                    await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, """{"daemon":null,"agents":null}"""), ct);
            };

            await using var server = new ScriptedServer(LocalSocketPaths.Socket(name), helloThen, subscribeDegenerate);

            var r = await LocalControlProbe.ProbeAsync(name, TimeSpan.FromSeconds(5));

            await Assert.That(r.Reachable).IsTrue();
            await Assert.That(r.Hello).IsNotNull();
            await Assert.That(r.Snapshot).IsNull();
            await Assert.That(r.IdentityConsistent).IsFalse();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }
}
