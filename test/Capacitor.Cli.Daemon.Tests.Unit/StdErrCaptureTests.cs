namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// on the detached launch path the CLI closes the daemon's std pipes,
/// so any output that bypasses the ILogger pipeline — the runtime's
/// "Fatal error." dump, an abort() message, a FailFast — is written to a broken
/// pipe and lost. The daemon reopens fds 1/2 onto a capture file when the CLI
/// passes <c>--stderr-file</c>. These cover the pure gate; the dup2 syscall
/// itself is a best-effort process side-effect verified by running the binary.
/// </summary>
public class StdErrCaptureTests {
    [Test]
    public async Task ResolveTarget_ReturnsNull_ForNullOrBlank() {
        await Assert.That(StdErrCapture.ResolveTarget(null)).IsNull();
        await Assert.That(StdErrCapture.ResolveTarget("")).IsNull();
        await Assert.That(StdErrCapture.ResolveTarget("   ")).IsNull();
    }

    [Test]
    public async Task ResolveTarget_ReturnsPath_WhenProvided_OnUnix() {
        // Runs on macOS/Linux here; on Windows the daemon uses a different
        // capture mechanism and ResolveTarget returns null.
        var expected = OperatingSystem.IsWindows() ? null : "/tmp/daemon.out.log";
        await Assert.That(StdErrCapture.ResolveTarget("/tmp/daemon.out.log")).IsEqualTo(expected);
    }
}
