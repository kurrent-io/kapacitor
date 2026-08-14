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

    /// <summary>HOME/lock-dir isolation, the stubbed launchctl manager/hello pair, and stderr
    /// capture shared by every test in this file — each test arranges only its own on-disk evidence
    /// shape (a malformed plist, stripped permissions, a directory, a dangling symlink, ...) before
    /// calling <see cref="RunStartVerifiedAsync"/>.</summary>
    sealed class ProdPathFixture : IDisposable {
        readonly string? _originalHome;
        readonly string _home;
        readonly TextWriter _originalErr = Console.Error;

        public string PlistPath => LaunchdUnit.PlistPath(Id);

        public LaunchdServiceManager Manager { get; } = new(
            runProcess: (_, args) => PrintNotFound(args),
            runBounded: (_, args, _) => {
                var (code, stdout, stderr) = PrintNotFound(args);
                return (code, stdout, stderr, false);
            });

        public ProdPathFixture() {
            _originalHome = Environment.GetEnvironmentVariable("HOME");
            _home = Directory.CreateTempSubdirectory("kcap-prodpath-home-").FullName;
            Environment.SetEnvironmentVariable("HOME", _home);

            var lockDir = Directory.CreateTempSubdirectory("kcap-prodpath-lock-").FullName;
            DaemonLockPaths.OverrideDirectoryForTesting(lockDir);
        }

        static (int ExitCode, string StdOut, string StdErr) PrintNotFound(string[] args) =>
            args[0] == "print"
                ? (113, "", $"Could not find service \"{LaunchdUnit.Label(Id)}\" in domain for user gui: 501")
                : (0, "", "");

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        public async Task<(int Exit, string[] StdErrLines)> RunStartVerifiedAsync() {
            var sut = new ServiceVerify(Manager, _ => 4242, Hello, TimeProvider.System,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            var capturedErr = new StringWriter();
            Console.SetError(capturedErr);
            int exit;
            try {
                exit = await sut.StartVerifiedAsync(Id);
            } finally {
                Console.SetError(_originalErr);
            }

            return (exit, capturedErr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Dispose() {
            Console.SetError(_originalErr);
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            Environment.SetEnvironmentVariable("HOME", _originalHome);
            try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Malformed_unit_on_disk_is_contained_through_the_real_manager_and_reaches_exit_28() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture();
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        File.WriteAllText(fx.PlistPath, DuplicateKeyPlist);

        // The `service status --json` style call: Query must NOT throw on this exact malformed
        // unit — only report unreadable binary evidence.
        var query = fx.Manager.Query(Id);
        await Assert.That(query.BinaryPath).IsNull();
        await Assert.That(query.UnitPresent).IsTrue();

        var (exit, _) = await fx.RunStartVerifiedAsync();

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
    }

    /// <summary>A unit that IS present but whose plist cannot be read must classify as
    /// <c>evidence_unreadable</c>, never the takeover-safe <c>directive_missing</c> a genuinely
    /// absent unit gets. Here: a real plist file with its read permission stripped —
    /// <c>File.Exists</c> still reports it present, but the open throws.</summary>
    [Test, NotInParallel]
    public async Task Present_but_unreadable_unit_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution and Unix file modes are POSIX-only");

        using var fx = new ProdPathFixture();
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        File.WriteAllText(fx.PlistPath, DuplicateKeyPlist); // content is irrelevant — the read never succeeds
        File.SetUnixFileMode(fx.PlistPath, UnixFileMode.None);

        try {
            // The unit-level presence signal Query reports must stay true — File.Exists sees the
            // file regardless of its permission bits.
            var query = fx.Manager.Query(Id);
            await Assert.That(query.UnitPresent).IsTrue();

            var (exit, lines) = await fx.RunStartVerifiedAsync();

            await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
            await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
        } finally {
            try { File.SetUnixFileMode(fx.PlistPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
    }

    /// <summary>A DIRECTORY at the plist path is present by every real signal but unreadable as
    /// content. <c>File.Exists</c> (which the presence check used to compose on) reads a
    /// directory as ABSENT — it only follows through to files — so this must still classify
    /// <c>evidence_unreadable</c>, never the takeover-safe <c>directive_missing</c>.</summary>
    [Test, NotInParallel]
    public async Task Directory_at_plist_path_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture();
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        Directory.CreateDirectory(fx.PlistPath); // a DIRECTORY sits at the plist path, not a file

        // The unit-level presence signal Query reports must stay true even though
        // File.Exists itself would say otherwise for a directory.
        var query = fx.Manager.Query(Id);
        await Assert.That(query.UnitPresent).IsTrue();

        var (exit, lines) = await fx.RunStartVerifiedAsync();

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
    }

    /// <summary>A dangling symlink AT the plist path — <c>File.Exists</c> reads it as absent (it
    /// follows through to the missing target), but the open raises <see cref="FileNotFoundException"/>
    /// with structural link evidence right at the path. Must classify <c>evidence_unreadable</c>,
    /// never <c>directive_missing</c>.</summary>
    [Test, NotInParallel]
    public async Task Dangling_symlink_at_plist_path_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture();
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        File.CreateSymbolicLink(fx.PlistPath, Path.Combine(LaunchdUnit.AgentsDir(), "never-created-target"));

        var (exit, lines) = await fx.RunStartVerifiedAsync();

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
    }

    /// <summary>A dangling symlink standing in for an ANCESTOR directory of the plist path (here,
    /// <c>~/Library</c> itself) raises <see cref="DirectoryNotFoundException"/> rather than
    /// <see cref="FileNotFoundException"/> — must still classify <c>evidence_unreadable</c>, never
    /// <c>directive_missing</c>.</summary>
    [Test, NotInParallel]
    public async Task Dangling_symlink_ancestor_of_plist_path_is_evidence_unreadable_not_directive_missing() {
        Skip.When(OperatingSystem.IsWindows(), "launchd/HOME-based plist resolution is POSIX-only");

        using var fx = new ProdPathFixture();
        var home = PathHelpers.HomeDirectory;
        var library = Path.Combine(home, "Library");
        File.CreateSymbolicLink(library, Path.Combine(home, "never-created-target"));

        var (exit, lines) = await fx.RunStartVerifiedAsync();

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        await Assert.That(lines).Contains("start_gate_reason=evidence_unreadable");
    }
}
