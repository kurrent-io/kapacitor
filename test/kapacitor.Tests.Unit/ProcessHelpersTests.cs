using System.Diagnostics;

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
        // Spawn a short-lived child process, capture its pid, wait for exit, then assert dead.
        using var process = new Process {
            StartInfo = new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            }
        };

        process.Start();
        var pid = process.Id;

        await Assert.That(ProcessHelpers.IsProcessAlive(pid)).IsTrue();

        await process.WaitForExitAsync();

        // Reap any zombie state so kill(pid, 0) reports ESRCH.
        // On Unix, the OS clears the process table entry when the parent waits.
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
