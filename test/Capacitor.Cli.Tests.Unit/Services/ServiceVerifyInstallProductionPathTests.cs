using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

// Real-manager counterpart to ServiceVerifyStartGateProductionPathTests, covering the same
// discriminated-read contract for InstallVerifiedAsync's marker recovery.
[NotInParallel(["HomeEnvVarMutation"])]
public class ServiceVerifyInstallProductionPathTests {
    const string Id = "prodpath-install";

    static ServiceSpec Spec(string daemonPath) =>
        new(Id, daemonPath, Path.Combine(Path.GetTempPath(), "prodpath-install-daemon.log"),
            new Dictionary<string, string>(), []);

    // File.Exists reads a directory as absent, so recovery must classify via the discriminated
    // read, not existence, or it would delete the marker instead of retaining it.
    [Test, NotInParallel]
    public async Task Leftover_marker_with_a_directory_at_the_plist_path_is_restore_verification_marker_retained() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture(Id);
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        Directory.CreateDirectory(fx.PlistPath); // a DIRECTORY sits at the plist path, not a file

        // The fingerprint value is irrelevant — recovery must never reach the fingerprint compare
        // once the read is classified Unreadable.
        ServiceTxnMarker.Write(fx.Store, Id, new TxnMarker(1, "install", "written", "stale", "no-unit", "irrelevant-fingerprint"));

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(fx.Store, fx.Config, fx.Manager, _ => 4242, Hello, TimeProvider.System);

        var exit = await sut.InstallVerifiedAsync(Spec(fx.DaemonPath), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
        await Assert.That(ServiceTxnMarker.Exists(fx.Store, Id)).IsTrue();
    }
}
