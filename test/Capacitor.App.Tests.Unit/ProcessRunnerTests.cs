using System.Diagnostics;
using System.Runtime.InteropServices;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Drives the REAL ProcessRunner (the System.Diagnostics.Process wrapper nested inside
/// DaemonClientService), unlike every other DaemonClientService test, which substitutes
/// FakeProcessRunner. IProcessRunner is a seam for DaemonClientService's own consumers, not for
/// ProcessRunner's guts, so a real child process is the only way to exercise the actual
/// stdout/stderr drain wiring.
///
/// Regression coverage for a Qodo review finding: the stdout drain task used to be discarded
/// (`_ = process.StandardOutput.ReadToEndAsync(ct)`), so a fault on it (e.g. once `ct` fired,
/// since it shared the caller's token) surfaced nowhere — an unobserved task exception. The fix
/// keeps both drain tasks, reads them with CancellationToken.None (so a detached child's pipes
/// never back up just because the caller stopped waiting), and awaits both via Task.WhenAll on
/// the normal path.
///
/// Not covered here: (1) asserting the abandoned-drain-observed property on the cancellation
/// path — that would require hooking the process-wide TaskScheduler.UnobservedTaskException
/// event and forcing GC/finalization on a non-deterministic schedule, which is not a reliable CI
/// signal and would pollute every other test in the process. (2) A large-output stress test to
/// prove no deadlock — analytically unnecessary: both ReadToEndAsync calls start consuming their
/// pipes immediately, concurrently with WaitForExitAsync, which is the standard
/// non-deadlocking pattern for redirected process IO; a real regression there would hang this
/// class's own tests until the CI timeout, which is still a signal, just not a fast one.
public class ProcessRunnerTests {
    static (string FileName, string[] Args) EchoBothStreamsThenExit(int exitCode) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", ["/c", $"echo out-marker & echo err-marker 1>&2 & exit {exitCode}"])
            : ("/bin/sh", ["-c", $"echo out-marker; echo err-marker >&2; exit {exitCode}"]);

    [Test]
    public async Task RunAsync_drains_both_streams_and_returns_the_exit_code() {
        var (fileName, args) = EchoBothStreamsThenExit(3);
        var runner = new DaemonClientService.ProcessRunner();

        var result = await runner.RunAsync(fileName, args, new RunOptions(), CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(3);
        await Assert.That(result.Stderr).Contains("err-marker");
    }

    [Test]
    public async Task RunAsync_reports_the_exit_code_on_success() {
        var (fileName, args) = EchoBothStreamsThenExit(0);
        var runner = new DaemonClientService.ProcessRunner();

        var result = await runner.RunAsync(fileName, args, new RunOptions(), CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_captures_stdout() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();

        var result = await runner.RunAsync("/bin/echo", ["hi"], new RunOptions(), CancellationToken.None);

        await Assert.That(result.Stdout).IsEqualTo("hi\n");
    }

    [Test]
    public async Task RunAsync_env_overlay_adds_without_clobbering_the_rest() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();
        var options = new RunOptions(EnvOverlay: new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" });

        var result = await runner.RunAsync("/usr/bin/env", [], options, CancellationToken.None);

        await Assert.That(result.Stdout).Contains("KCAP_PROFILE=work");
        await Assert.That(result.Stdout).Contains("PATH="); // overlay adds, never replaces the inherited env
    }

    [Test]
    public async Task Timeout_kills_the_tree_and_returns_promptly() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();
        var sw = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            "/bin/sleep", ["30"], new RunOptions(Timeout: TimeSpan.FromMilliseconds(200)), CancellationToken.None);
        sw.Stop();

        await Assert.That(result.TimedOut).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task AbandonWait_cancelled_ct_throws_and_the_child_survives() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var marker = Path.Combine(Path.GetTempPath(), $"kcap-processrunner-{Guid.NewGuid():N}");
        try {
            var runner = new DaemonClientService.ProcessRunner();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync("/bin/sh", ["-c", $"sleep 0.3; touch {marker}"], new RunOptions(), cts.Token));

            // Not killed: still running at the point the wait was abandoned, so it hasn't
            // reached `touch` yet — then it finishes on its own past the abandoned wait.
            await Assert.That(File.Exists(marker)).IsFalse();
            await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(5), "the abandoned child to finish and touch the marker");
        } finally {
            File.Delete(marker);
        }
    }

    [Test]
    public async Task KillTree_cancelled_ct_kills_the_child_then_throws() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var marker = Path.Combine(Path.GetTempPath(), $"kcap-processrunner-{Guid.NewGuid():N}");
        try {
            var runner = new DaemonClientService.ProcessRunner();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(
                    "/bin/sh", ["-c", $"sleep 0.3; touch {marker}"],
                    new RunOptions(CancelMode: CancelMode.KillTree), cts.Token));

            // Killed before it could reach `touch` — unlike AbandonWait, it never gets there.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(File.Exists(marker)).IsFalse();
        } finally {
            File.Delete(marker);
        }
    }

    [Test]
    public async Task ProcessOnly_timeout_kills_the_shell_but_spares_the_grandchild() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();
        // Grandchild redirects its own stdio away from the inherited pipe, like a detached daemon.
        var result = await runner.RunAsync(
            "/bin/sh", ["-c", "sleep 30 >/dev/null 2>&1 & echo $!; wait"],
            new RunOptions(Timeout: TimeSpan.FromMilliseconds(500), TimeoutKill: TimeoutKillScope.ProcessOnly),
            CancellationToken.None);

        await Assert.That(result.TimedOut).IsTrue();
        var grandchildPid = int.Parse(result.Stdout.Trim());
        try {
            var grandchild = Process.GetProcessById(grandchildPid); // throws if already dead
            await Assert.That(grandchild.HasExited).IsFalse();
        } finally {
            try { Process.GetProcessById(grandchildPid).Kill(); }
            catch (ArgumentException) { /* already gone */ }
        }
    }

    [Test]
    public async Task Tree_timeout_kills_the_grandchild_too() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();
        var result = await runner.RunAsync(
            "/bin/sh", ["-c", "sleep 30 & echo $!; wait"],
            new RunOptions(Timeout: TimeSpan.FromMilliseconds(500), TimeoutKill: TimeoutKillScope.Tree),
            CancellationToken.None);

        await Assert.That(result.TimedOut).IsTrue();
        var grandchildPid = int.Parse(result.Stdout.Trim());
        await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(5), "the grandchild to die with the tree");
    }

    [Test]
    public async Task KillTree_cancellation_kills_the_tree_even_with_TimeoutKill_ProcessOnly() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new DaemonClientService.ProcessRunner();
        using var cts = new CancellationTokenSource();
        var startedMarker = Path.Combine(Path.GetTempPath(), $"kcap-processrunner-{Guid.NewGuid():N}");
        int grandchildPid = -1;
        try {
            var runTask = runner.RunAsync(
                "/bin/sh", ["-c", $"sleep 30 & echo $! > {startedMarker}; wait"],
                new RunOptions(CancelMode: CancelMode.KillTree, TimeoutKill: TimeoutKillScope.ProcessOnly),
                cts.Token);

            await WaitUntilAsync(() => File.Exists(startedMarker), TimeSpan.FromSeconds(5), "the grandchild to start and record its PID");
            grandchildPid = int.Parse((await File.ReadAllTextAsync(startedMarker)).Trim());
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
            await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(5), "the grandchild to die with the caller-cancelled tree");
        } finally {
            File.Delete(startedMarker);
            if (grandchildPid > 0) {
                try { Process.GetProcessById(grandchildPid).Kill(); }
                catch (ArgumentException) { /* already gone */ }
            }
        }
    }

    static bool IsAlive(int pid) {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
