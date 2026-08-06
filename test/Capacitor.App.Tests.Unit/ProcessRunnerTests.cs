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

        var (exitCode, stderr) = await runner.RunAsync(fileName, args, CancellationToken.None);

        await Assert.That(exitCode).IsEqualTo(3);
        await Assert.That(stderr).Contains("err-marker");
    }

    [Test]
    public async Task RunAsync_reports_the_exit_code_on_success() {
        var (fileName, args) = EchoBothStreamsThenExit(0);
        var runner = new DaemonClientService.ProcessRunner();

        var (exitCode, _) = await runner.RunAsync(fileName, args, CancellationToken.None);

        await Assert.That(exitCode).IsEqualTo(0);
    }
}
