using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Real-process coverage for spec §7 "parent death &amp; stdio": guarantees fakes can't prove — the
/// --verify transaction never hangs or crashes when its stdio pipes close, and the service flock is
/// released when the process exits, even orphaned. Drives <c>kcap daemon service start --verify</c>
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

    /// <summary>First direct child pid of <paramref name="parentPid"/>, via <c>pgrep -P</c> — used to
    /// find the orphaned kcap grandchild for a best-effort cleanup kill, since Process.Start only
    /// hands back a handle to the immediate /bin/sh child.</summary>
    static int? FindChildPid(int parentPid) {
        try {
            var psi = new ProcessStartInfo("pgrep", $"-P {parentPid}") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var firstLine = p.StandardOutput.ReadLine();
            p.WaitForExit(2000);
            return firstLine is not null && int.TryParse(firstLine, out var pid) ? pid : null;
        } catch { return null; }
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

        // Drop our ends of every stdio pipe immediately, before the child has written anything. On
        // this platform Console writes into a reader-less pipe are silently dropped below the managed
        // exception layer (verified empirically — .NET's Unix console PAL absorbs the broken-pipe
        // signal), so this mainly guards against a hang or an uncaught crash from any write path that
        // does NOT go through Console (i.e. bypasses Say()'s own IOException guard).
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

        // No daemon/plist exists in this isolated env, so start-verify always runs its full
        // forward-budget poll then rolls back — ReadinessTimeout(24) is the deterministic outcome;
        // RollbackBudget(26)/RestoreVerification(27) cover the rare Unknown-probe timing variant.
        // Never Contended(20) (lock is uncontended) and never a signal-death code (128+).
        await Assert.That(process.ExitCode).IsGreaterThanOrEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(process.ExitCode).IsLessThanOrEqualTo(VerifyExit.RestoreVerification);
    }

    [Test]
    public async Task ParentDeath_OrphanedChildStillReleasesTheServiceLock() {
        Skip.When(!OperatingSystem.IsMacOS(), "exercises launchctl-classifying code paths");

        var binary = RequireCliBinary();
        var (home, daemons, config) = NewIsolatedEnv();
        const string serviceName = "ptest2";

        // kcap's own stderr is redirected to a file by the SHELL before it execs kcap, so the fd stays
        // valid (and the writes land) independent of our C# Process handle for /bin/sh, and independent
        // of /bin/sh itself dying — this is what lets the lock-release assertion below distinguish a
        // real coded rollback exit from "the kernel dropped the flock because something crashed".
        var errPath = Path.Combine(home, "verify.err");

        // sh is the "parent" — it forks kcap as its own child (no exec) so killing sh orphans kcap
        // rather than replacing it. The trailing `echo done` is what forces sh to stay a real
        // intermediate process instead of tail-call-optimizing itself away.
        var shellCommand = $"'{binary}' daemon service start --name {serviceName} --verify 2>'{errPath}'; echo done";
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

        // Find the orphan before killing its parent, so a failure below (the exact regression this
        // test exists to catch — a hung orphan) doesn't leak a stray kcap process past this test.
        var orphanPid = FindChildPid(shell.Id);

        try { shell.Kill(); } catch { /* already gone */ }
        try {
            using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await shell.WaitForExitAsync(reapCts.Token);
        } catch { /* best effort */ }

        try {
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

            // The kernel drops the flock on ANY process exit, crash included — reading kcap's own
            // stderr (captured independently of the now-dead shell, see errPath above) is what tells
            // "reached a coded rollback exit" apart from "the orphan crashed and released it that way".
            var stderr = File.Exists(errPath) ? await File.ReadAllTextAsync(errPath) : "";
            var reachedCodedExit = stderr.Contains(VerifyExit.ReadinessTimeoutToken)
                || stderr.Contains(VerifyExit.RollbackBudgetToken)
                || stderr.Contains(VerifyExit.RestoreVerificationToken);
            await Assert.That(reachedCodedExit).IsTrue();
        } finally {
            if (orphanPid is { } pid) {
                try {
                    using var orphan = Process.GetProcessById(pid);
                    if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
                } catch { /* already gone, or pid recycled — best effort */ }
            }
        }
    }
}
