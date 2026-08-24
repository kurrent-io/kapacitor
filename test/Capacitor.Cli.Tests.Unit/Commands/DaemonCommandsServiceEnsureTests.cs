using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Dispatch rows of <c>kcap daemon service ensure</c> that need no real service manager: the
/// already-enabled and fail-closed-attention rows are pure classification reads. (The install/start
/// arms on launchd run the ServiceVerify transaction — covered by the engine's own suites; the
/// plain arms need the daemon binary present, which a unit test host does not have.)
/// </summary>
/// <remarks>Bare <c>[NotInParallel]</c>: <c>Ensure</c> writes to the process-global
/// <c>Console.Out</c>, so these cannot run beside a test capturing console streams.</remarks>
[NotInParallel]
public class DaemonCommandsServiceEnsureTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    sealed class FakeManager : IServiceManager {
        public ServiceQuery QueryResult { get; init; } =
            new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        public string Describe() => "fake";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);
        public ServiceQuery Query(string serviceId) => QueryResult;
        public void Install(ServiceSpec spec, bool startNow) { }
        public void WriteAndBootstrap(ServiceSpec spec) { }
        public bool Uninstall(string serviceId, out string? error) { error = null; return true; }
        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }

    [Test]
    public async Task Unknown_probe_fails_closed_with_reason() {
        var manager = new FakeManager {
            QueryResult = new ServiceQuery(LabelProbe.Unknown, false, ServiceState.NotInstalled, null, null)
        };
        var exit = await new DaemonServiceCommands(Daemons.Store, manager, "test-id").Ensure(["--json"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Active_transaction_reports_attention_without_mutating() {
        var manager = new FakeManager {
            QueryResult = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/b/kcap-daemon", 42)
        };
        // Ensure reads the lock via ServiceTxnLock.IsHeld; hold it for real.
        using var held = ServiceTxnLock.TryAcquire(Daemons.Store, "test-id", TimeSpan.FromSeconds(1));
        await Assert.That(held).IsNotNull();

        var exit = await new DaemonServiceCommands(Daemons.Store, manager, "test-id").Ensure(["--json"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    // F1: on launchd a unit baked without a resolvable profile can never pass the start gate's
    // identity half (it demands a non-empty invoking KCAP_PROFILE), so the install arm must fail
    // closed up front rather than report a success that can never be gated-started. Driven with a
    // real LaunchdServiceManager whose launchctl calls are stubbed to "no such service" — the
    // refusal fires before any transaction work.
    [Test]
    public async Task Launchd_install_without_a_profile_fails_closed() {
        Capacitor.Cli.Core.Config.AppConfig.ResetResolvedStateForTesting();
        var manager = new LaunchdServiceManager(
            runProcess: (_, _) => (1, "", "Could not find service: io.kurrent.kcap.daemon.test-id"),
            runBounded: (_, _, _) => (1, "", "Could not find service: io.kurrent.kcap.daemon.test-id", false));

        using var capture = ConsoleOutput.StartCapture();
        var exit = await new DaemonServiceCommands(Daemons.Store, manager, "test-id").Ensure(["--json"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedOutput()).Contains("no_profile_configured");
    }
}
