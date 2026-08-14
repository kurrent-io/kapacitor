using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Finding #1 of the PR #554 round-3 review: <c>LaunchdServiceManager.QueryCore</c> used to parse
/// the plist's binary evidence directly, so round-2's own throwing leaf parsers (malformed XML, a
/// duplicate <c>ProgramArguments</c> key) escaped <c>manager.Query</c> as UNCODED failures — both
/// before the start gate's own <c>gated</c> determination, and post-mutation. <see
/// cref="ServiceVerifyStartTests"/> already pins the leaf-parser-throws contract and the gate's OWN
/// contained Phase-A re-parse (via a stubbed <c>readPlist</c> seam on a Fake manager); this class
/// proves the fix end to end through the REAL <see cref="LaunchdServiceManager"/> — the exact
/// combination the finding calls out as "not just the leaf parser test".
/// </summary>
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
}
