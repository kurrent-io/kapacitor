using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Real-process coverage for spec §7 "parent death &amp; stdio": guarantees fakes can't prove — the
/// --verify transaction still completes its coded exit when stdio pipes close, and the service flock
/// is released when the process exits, even orphaned. Drives <c>kcap daemon service start --verify</c>
/// against an isolated temp HOME/KCAP_DAEMONS_DIR with no daemon/plist present, so the transaction
/// runs a real fail-fast path (lock → launchctl query → bootstrap attempt → forward-budget poll →
/// rollback) without touching anything real. macOS-only: launchctl-classifying code paths.
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceVerifyProcessTests : IDisposable {
    readonly List<string> _tempDirs = [];

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    string NewTempDir(string prefix) {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    (string Home, string Daemons, string Config) NewIsolatedEnv() => (
        NewTempDir("kcap-verify-home"),
        Path.Combine(NewTempDir("kcap-verify-daemons"), "daemons"),
        NewTempDir("kcap-verify-cfg")
    );

    /// <summary>Mirrors McpSessionsServerTests.GetCliBinaryPath: walk up from the test assembly's own
    /// bin dir to the repo root, then down into the CLI project's build output.</summary>
    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(ServiceVerifyProcessTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";

        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    static string RequireCliBinary() {
        var binary = GetCliBinaryPath();
        if (!File.Exists(binary)) {
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary);
        }
        return binary;
    }

    static bool LockHeld(string daemonsDir, string serviceName) {
        DaemonLockPaths.OverrideDirectoryForTesting(daemonsDir);
        try { return ServiceTxnLock.IsHeld(serviceName); }
        finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task ClosedStdio_StillExitsWithCodedNonZero() {
        Skip.When(!OperatingSystem.IsMacOS(), "exercises launchctl-classifying code paths");

        var binary = RequireCliBinary();
        var (home, daemons, config) = NewIsolatedEnv();

        var psi = new ProcessStartInfo(binary, "daemon service start --name ptest --verify") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = home,
            Environment = {
                ["HOME"]             = home,
                ["KCAP_DAEMONS_DIR"] = daemons,
                ["KCAP_CONFIG_DIR"]  = config,
            }
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap");

        // The scenario under test: drop our ends of every stdio pipe immediately, before the child
        // has written anything. A write into a reader-less pipe raises IOException in managed code
        // (the runtime ignores SIGPIPE) — ServiceVerify's Say() must swallow that, not crash or hang.
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try {
            await process.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("kcap did not exit within 60s after its stdio pipes closed.");
        }

        // No daemon/plist exists in this isolated env, so the transaction always reaches a coded
        // rollback exit (VerifyExit 20-27), never Ok(0) — and never a signal-death code (128+).
        await Assert.That(process.ExitCode).IsGreaterThanOrEqualTo(VerifyExit.Contended);
        await Assert.That(process.ExitCode).IsLessThanOrEqualTo(VerifyExit.RestoreVerification);
    }

    [Test]
    public async Task ParentDeath_OrphanedChildStillReleasesTheServiceLock() {
        Skip.When(!OperatingSystem.IsMacOS(), "exercises launchctl-classifying code paths");

        var binary = RequireCliBinary();
        var (home, daemons, config) = NewIsolatedEnv();
        const string serviceName = "ptest2";

        // sh is the "parent" — it forks kcap as its own child (no exec) so killing sh orphans kcap
        // rather than replacing it. The trailing `echo done` is what forces sh to stay a real
        // intermediate process instead of tail-call-optimizing itself away.
        var shellCommand = $"'{binary}' daemon service start --name {serviceName} --verify; echo done";
        var psi = new ProcessStartInfo("/bin/sh", ["-c", shellCommand]) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = home,
            Environment = {
                ["HOME"]             = home,
                ["KCAP_DAEMONS_DIR"] = daemons,
                ["KCAP_CONFIG_DIR"]  = config,
            }
        };

        using var shell = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start /bin/sh");

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Confirm the transaction actually took the lock before killing the parent — otherwise an
        // unheld lock below would be vacuously true rather than evidence of a release.
        var acquireDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!LockHeld(daemons, serviceName) && DateTime.UtcNow < acquireDeadline)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        await Assert.That(LockHeld(daemons, serviceName)).IsTrue();

        try { shell.Kill(); } catch { /* already gone */ }
        try {
            using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await shell.WaitForExitAsync(reapCts.Token);
        } catch { /* best effort */ }

        // Hard guarantee under test: the orphaned kcap grandchild finishes its transaction and
        // releases the lock on its own — no parent left to reap it or notice it hung. Marker state
        // is diagnostic only, not asserted: a fast-fail path may leave no marker at all, while a
        // failure during rollback-restore may legitimately retain one — both are valid terminals.
        var releaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        var released = false;
        while (DateTime.UtcNow < releaseDeadline) {
            if (!LockHeld(daemons, serviceName)) { released = true; break; }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        await Assert.That(released).IsTrue();
    }
}
