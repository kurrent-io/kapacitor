using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`install --verify` is a launchd-only slice: the engine needs a manager whose
/// WriteAndBootstrap actually classifies/mutates per the verify algorithm, and the on-disk recheck
/// needs GenerateFiles to return exactly one file. Non-launchd managers get a clear, coded-nowhere
/// rejection rather than a deep failure inside the transaction.</summary>
public class DaemonCommandsServiceInstallTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Verify_flag_is_rejected_on_a_non_launchd_manager() {
        var exit = await new DaemonServiceCommands(Daemons.Store, new SystemdServiceManager(), "test-id").Install(["--verify"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Verify_flag_is_rejected_on_the_windows_manager_too() {
        var exit = await new DaemonServiceCommands(Daemons.Store, new WindowsScheduledTaskServiceManager(), "test-id").Install(["--verify"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>--replace only has meaning inside the verify transaction engine (it selects
    /// ServiceVerify.InstallVerifiedAsync's ownership matrix) — a plain install has no transaction
    /// to hand it to, so the combination is rejected before even reaching the launchd-only gate
    /// (asserted here on a non-launchd manager, which would otherwise reject for a different
    /// reason).</summary>
    [Test]
    public async Task Replace_without_verify_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, new SystemdServiceManager(), "test-id").Install(["--replace"], true);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>--no-start withholds the start; --verify's job is to prove the started daemon is
    /// ready. The two contradict and are rejected before anything runs (startNow=false models
    /// --no-start).</summary>
    [Test]
    public async Task No_start_with_verify_is_rejected() {
        var exit = await new DaemonServiceCommands(Daemons.Store, new SystemdServiceManager(), "test-id").Install(["--verify", "--no-start"], startNow: false);
        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>A plain (non-verify) install serializes on the same per-label service lock as every
    /// other mutating verb: a held lock yields the coded-contention exit without ever calling
    /// Install.</summary>
    [Test]
    public async Task Plain_install_bails_on_a_held_service_lock_without_calling_install() {
        const string id = "svc-plain-install-lock";
        using var held = ServiceTxnLock.TryAcquire(Daemons.Store, id, TimeSpan.FromSeconds(1));
        await Assert.That(held).IsNotNull();

        var manager = new CountingManager();
        var spec = new ServiceSpec(id, "/x/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

        var exit = await new DaemonServiceCommands(Daemons.Store, manager, "test-id").InstallPlain(spec, startNow: true);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(manager.InstallCalls).IsEqualTo(0);
    }

    sealed class CountingManager : IServiceManager {
        public int InstallCalls;
        public string Describe() => "counting";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);
        public ServiceQuery Query(string serviceId) => new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        public void Install(ServiceSpec spec, bool startNow) => InstallCalls++;
        public void WriteAndBootstrap(ServiceSpec spec) { }
        public bool Uninstall(string serviceId, out string? error) { error = null; return true; }
        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }
}
