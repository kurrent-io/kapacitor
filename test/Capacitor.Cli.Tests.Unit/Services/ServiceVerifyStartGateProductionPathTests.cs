using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Proves <see cref="LaunchdServiceManager"/>'s default, unstubbed plist read never lets
/// a malformed or unreadable on-disk unit escape the start gate as an uncoded failure — end to end
/// through the REAL manager, complementing <see cref="ServiceVerifyStartTests"/>'s stubbed-seam
/// coverage of the same contract.</summary>
[NotInParallel(["HomeEnvVarMutation", nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting"])]
public class ServiceVerifyStartGateProductionPathTests {
    const string Id = "prodpath";

    // A duplicate top-level ProgramArguments key — never written by LaunchdUnit.Plist itself, so
    // this can only be a foreign/corrupt writer — with the gate's own consent directive baked so
    // Phase A actually has evidence to re-parse.
    const string DuplicateKeyPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.prodpath</string>
          <key>ProgramArguments</key><array>
            <string>/bin/kcap-daemon</string>
          </array>
          <key>ProgramArguments</key><array>
            <string>/bin/evil-daemon</string>
          </array>
          <key>EnvironmentVariables</key><dict>
            <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
          </dict>
        </dict>
        </plist>
        """;

    static (int ExitCode, string StdOut, string StdErr) PrintNotFound(string[] args) =>
        args[0] == "print"
            ? (113, "", "Could not find service \"io.kurrent.kcap.daemon.prodpath\" in domain for user gui: 501")
            : (0, "", "");

    [Test]
    public async Task Malformed_unit_on_disk_is_contained_through_the_real_manager_and_reaches_exit_28() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var home = Directory.CreateTempSubdirectory("kcap-prodpath-home-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);

        var lockDir = Directory.CreateTempSubdirectory("kcap-prodpath-lock-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(lockDir);

        try {
            Directory.CreateDirectory(LaunchdUnit.AgentsDir());
            File.WriteAllText(LaunchdUnit.PlistPath(Id), DuplicateKeyPlist);

            // Both launchctl seams stubbed (Query is called with and without a timeout across this
            // test) so no real launchctl process is ever invoked — this test proves the FILE-parsing
            // containment, not launchd interaction.
            var manager = new LaunchdServiceManager(
                runProcess: (_, args) => PrintNotFound(args),
                runBounded: (_, args, _) => {
                    var (code, stdout, stderr) = PrintNotFound(args);
                    return (code, stdout, stderr, false);
                });

            // The `service status --json` style call: Query must NOT throw on this exact malformed
            // unit — only report unreadable binary evidence.
            var query = manager.Query(Id);
            await Assert.That(query.BinaryPath).IsNull();
            await Assert.That(query.UnitPresent).IsTrue();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            // Default readPlist (real File.ReadAllText) and the real manager — the gate's own
            // contained Phase-A parse is the sole authority for coded evidence classification.
            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A unit that IS present but whose plist cannot be read must classify as
    /// <c>evidence_unreadable</c>, never the takeover-safe <c>directive_missing</c> a genuinely
    /// absent unit gets. Here: a real plist file with its read permission stripped —
    /// <c>File.Exists</c> still reports it present, but the open throws.</summary>
    [Test, NotInParallel]
    public async Task Present_but_unreadable_unit_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution and Unix file modes are POSIX-only");

        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var home = Directory.CreateTempSubdirectory("kcap-prodpath-home-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);

        var lockDir = Directory.CreateTempSubdirectory("kcap-prodpath-lock-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(lockDir);

        var originalErr = Console.Error;
        var capturedErr = new StringWriter();

        try {
            Directory.CreateDirectory(LaunchdUnit.AgentsDir());
            var plistPath = LaunchdUnit.PlistPath(Id);
            File.WriteAllText(plistPath, DuplicateKeyPlist); // content is irrelevant — the read never succeeds
            File.SetUnixFileMode(plistPath, UnixFileMode.None);

            var manager = new LaunchdServiceManager(
                runProcess: (_, args) => PrintNotFound(args),
                runBounded: (_, args, _) => {
                    var (code, stdout, stderr) = PrintNotFound(args);
                    return (code, stdout, stderr, false);
                });

            // The unit-level presence signal Query reports must stay true — File.Exists sees the
            // file regardless of its permission bits.
            var query = manager.Query(Id);
            await Assert.That(query.UnitPresent).IsTrue();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            Console.SetError(capturedErr);
            var exit = await sut.StartVerifiedAsync(Id);
            Console.SetError(originalErr);

            await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
            var lines = capturedErr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
        } finally {
            Console.SetError(originalErr);
            try { File.SetUnixFileMode(LaunchdUnit.PlistPath(Id), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A DIRECTORY at the plist path is present by every real signal but unreadable as
    /// content. <c>File.Exists</c> (which the presence check used to compose on) reads a
    /// directory as ABSENT — it only follows through to files — so this must still classify
    /// <c>evidence_unreadable</c>, never the takeover-safe <c>directive_missing</c>.</summary>
    [Test, NotInParallel]
    public async Task Directory_at_plist_path_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var home = Directory.CreateTempSubdirectory("kcap-prodpath-home-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);

        var lockDir = Directory.CreateTempSubdirectory("kcap-prodpath-lock-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(lockDir);

        var originalErr = Console.Error;
        var capturedErr = new StringWriter();

        try {
            Directory.CreateDirectory(LaunchdUnit.AgentsDir());
            Directory.CreateDirectory(LaunchdUnit.PlistPath(Id)); // a DIRECTORY sits at the plist path, not a file

            var manager = new LaunchdServiceManager(
                runProcess: (_, args) => PrintNotFound(args),
                runBounded: (_, args, _) => {
                    var (code, stdout, stderr) = PrintNotFound(args);
                    return (code, stdout, stderr, false);
                });

            // The unit-level presence signal Query reports must stay true even though
            // File.Exists itself would say otherwise for a directory.
            var query = manager.Query(Id);
            await Assert.That(query.UnitPresent).IsTrue();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            Console.SetError(capturedErr);
            var exit = await sut.StartVerifiedAsync(Id);
            Console.SetError(originalErr);

            await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
            var lines = capturedErr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
        } finally {
            Console.SetError(originalErr);
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }
}
