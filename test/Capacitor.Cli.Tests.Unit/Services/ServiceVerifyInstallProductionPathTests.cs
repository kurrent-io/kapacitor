using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Proves entry-time marker recovery's discriminated plist read never treats a structural
/// obstruction (a directory sitting at the plist path) as confirmed-absent residue — end to end
/// through the REAL <see cref="LaunchdServiceManager"/>, mirroring
/// <see cref="ServiceVerifyStartGateProductionPathTests"/>'s coverage of the same contract for
/// <c>StartVerifiedAsync</c>'s Phase A.</summary>
[NotInParallel(["HomeEnvVarMutation", nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting"])]
public class ServiceVerifyInstallProductionPathTests {
    const string Id = "prodpath-install";

    sealed class ProdPathFixture : IDisposable {
        readonly string? _originalHome;
        readonly string _home;
        readonly string _lockDir;

        public string PlistPath  => LaunchdUnit.PlistPath(Id);
        public string DaemonPath { get; }

        public LaunchdServiceManager Manager { get; } = new(
            runProcess: (_, args) => PrintNotFound(args),
            runBounded: (_, args, _) => {
                var (code, stdout, stderr) = PrintNotFound(args);
                return (code, stdout, stderr, false);
            });

        public ProdPathFixture() {
            _originalHome = Environment.GetEnvironmentVariable("HOME");
            _home = Directory.CreateTempSubdirectory("kcap-prodpath-install-home-").FullName;
            Environment.SetEnvironmentVariable("HOME", _home);

            _lockDir = Directory.CreateTempSubdirectory("kcap-prodpath-install-lock-").FullName;
            DaemonLockPaths.OverrideDirectoryForTesting(_lockDir);

            DaemonPath = Path.Combine(_lockDir, "kcap-daemon");
            File.WriteAllText(DaemonPath, "");
        }

        static (int ExitCode, string StdOut, string StdErr) PrintNotFound(string[] args) =>
            args[0] == "print"
                ? (113, "", $"Could not find service \"{LaunchdUnit.Label(Id)}\" in domain for user gui: 501")
                : (0, "", "");

        public void Dispose() {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            Environment.SetEnvironmentVariable("HOME", _originalHome);
            try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        }
    }

    static ServiceSpec Spec(string daemonPath) =>
        new(Id, daemonPath, Path.Combine(Path.GetTempPath(), "prodpath-install-daemon.log"),
            new Dictionary<string, string>(), []);

    /// <summary>A DIRECTORY at the plist path is present by every real signal but unreadable as
    /// content — <c>File.Exists</c> reads a directory as ABSENT, so a leftover-marker recovery that
    /// composed <c>_readPlist</c>/<c>_plistExists</c> separately would misclassify it as its own
    /// gone residue and delete the marker. Must surface RestoreVerification with the marker
    /// retained instead.</summary>
    [Test, NotInParallel]
    public async Task Leftover_marker_with_a_directory_at_the_plist_path_is_restore_verification_marker_retained() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture();
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        Directory.CreateDirectory(fx.PlistPath); // a DIRECTORY sits at the plist path, not a file

        // The fingerprint value is irrelevant — recovery must never reach the fingerprint compare
        // once the read is classified Unreadable.
        ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "written", "stale", "no-unit", "irrelevant-fingerprint"));

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(fx.Manager, _ => 4242, Hello, TimeProvider.System);

        var exit = await sut.InstallVerifiedAsync(Spec(fx.DaemonPath), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
        await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
    }
}
