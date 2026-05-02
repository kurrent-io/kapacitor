using System.Diagnostics;
using System.Runtime.InteropServices;

namespace kapacitor.Tests.Unit;

public class ProcessHelpersTests {
    [Test]
    public async Task IsProcessAlive_returns_true_for_current_process() {
        var alive = ProcessHelpers.IsProcessAlive(Environment.ProcessId);

        await Assert.That(alive).IsTrue();
    }

    [Test]
    public async Task IsProcessAlive_returns_false_for_pid_zero_or_one() {
        // pid 0 is invalid; pid 1 is init/launchd which we treat as "no parent" by convention.
        await Assert.That(ProcessHelpers.IsProcessAlive(0)).IsFalse();
        await Assert.That(ProcessHelpers.IsProcessAlive(1)).IsFalse();
    }

    [Test]
    public async Task IsProcessAlive_returns_false_for_negative_pid() {
        await Assert.That(ProcessHelpers.IsProcessAlive(-1)).IsFalse();
    }

    [Test]
    public async Task IsProcessAlive_transitions_to_false_after_child_exits() {
        // Use a long-running child and Kill() it explicitly: a fast-exit command can be
        // reaped by .NET's SIGCHLD handler before the alive assertion runs on a busy CI
        // scheduler, making `kill(pid, 0)` return ESRCH and the test fail intermittently.
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;

        using var process = new Process { StartInfo = psi };
        process.Start();
        var pid = process.Id;

        await Assert.That(ProcessHelpers.IsProcessAlive(pid)).IsTrue();

        process.Kill();
        await process.WaitForExitAsync();

        // After .NET reaps the killed child, kill(pid, 0) returns ESRCH. Poll briefly
        // in case the reaper runs slightly later than WaitForExitAsync's completion.
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline && ProcessHelpers.IsProcessAlive(pid)) {
            await Task.Delay(50);
        }

        await Assert.That(ProcessHelpers.IsProcessAlive(pid)).IsFalse();
    }

    [Test]
    public async Task GetParentPid_returns_a_live_process() {
        var ppid = ProcessHelpers.GetParentPid();

        await Assert.That(ppid).IsNotNull();
        await Assert.That(ProcessHelpers.IsProcessAlive(ppid!.Value)).IsTrue();
    }
}
