using System.Globalization;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>L1-managed(b): the REAL production entry point (UnixPtyProcessFactory.Spawn) end
/// to end — resolves PATH in the parent, builds a plan via pty_preflight, spawns via the
/// dedicated spawner thread, and surfaces the natively-captured StartIdentity.</summary>
public class UnixPtyProcessSpawnTests {
    [Test]
    public async Task Spawn_produces_a_running_process_with_a_captured_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // The spawner thread is a non-background (IsBackground = false) OS thread — the test
        // must own it and Dispose() it itself, or the thread parks forever on the queue and the
        // whole test-host process never exits (confirmed empirically: an earlier version of this
        // test that let UnixPtyProcessFactory own an undisposed static singleton hung indefinitely).
        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner);
        var       proc    = factory.Spawn("sleep", ["5"], Directory.GetCurrentDirectory());
        try {
            await Assert.That(proc.Pid).IsGreaterThan(0);
            await Assert.That(proc.StartIdentity).IsNotNull();
            await Assert.That(proc.StartIdentity).IsNotEmpty();
            await Assert.That(proc.StartIdentity).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");

            // Cross-check against the independent, existing ProcessStartToken machinery: on
            // Linux both the shim's capture_lx_identity (pty_shim.c) and ProcessStartToken.ForPid
            // read the SAME two kernel facts (starttime field 22 of /proc/{pid}/stat, and
            // /proc/sys/kernel/random/boot_id) in the SAME "lx:{boot}:{starttime}" format via two
            // independently-implemented code paths, so they must produce byte-identical tokens
            // for a healthy spawn.
            //
            // On macOS, as of M1-A(a) [Task 7], ProcessStartToken.ForPid ALSO vendors the SAME
            // proc_pidinfo(PROC_PIDUNIQIDENTIFIERINFO=17) + kern.bootsessionuuid capture pty_shim.c's
            // pty_capture_mac_identity uses natively (same fixed-offset prefix read, same 256-byte
            // buffer size) — so the two independently-implemented code paths must ALSO produce
            // byte-identical "mac:{bootsessionuuid}:{p_uniqueid}" tokens for a healthy spawn. This
            // is the regression guard against the two vendored copies of the private struct
            // drifting apart (before Task 7 landed, Core's non-Linux branch returned the OLDER
            // "tk:{Process.StartTime.Ticks}" scheme instead, so this cross-check did not apply on
            // macOS and was gated out below).
            var liveToken = Capacitor.Cli.Core.ProcessStartToken.ForPid(proc.Pid);
            await Assert.That(liveToken).IsEqualTo(proc.StartIdentity);
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>Terminate must take down the leader's whole process group, not just the leader —
    /// the shape a codex reviewer takes with its code-mode-host helper (kcap-cli#469). The shell
    /// leader backgrounds a long sleep (same group, since the leader is a forkpty session leader),
    /// reports its pid over the PTY, and waits. A leader-only kill leaves the sleep orphaned and
    /// running, which is exactly the mutation this test exists to fail on.</summary>
    [Test]
    public async Task Terminate_kills_the_leaders_whole_process_group() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner);
        // The helper IGNORES SIGHUP (trap '' HUP survives the exec): a plain background sleep dies
        // to the controlling terminal's leader-exit SIGHUP even under a leader-only kill, which let
        // exactly that mutation pass this test — a helper that shrugs off HUP is also the shape
        // that leaks in production. Only a signal to the GROUP reaches it.
        var proc = factory.Spawn(
            "/bin/sh", ["-c", "(trap '' HUP; exec sleep 300) & echo \"CHILD:$!:DONE\"; wait"],
            Directory.GetCurrentDirectory());
        try {
            var childPid = await ReadReportedChildPidAsync(proc);
            await Assert.That(childPid).IsGreaterThan(0);
            // Capture while it is provably alive: this is init's child once the leader dies, not
            // ours, so the pid alone would read as live again the moment the OS reassigns it.
            var childIdentity = PidIdentity.Capture(childPid);

            await proc.TerminateAsync(TimeSpan.FromSeconds(5));
            await Assert.That(proc.HasExited).IsTrue();

            // Reparented to init, which reaps it once it dies — poll for the identity leaving the
            // process table rather than racing the reap.
            await PidIdentity.WaitUntilGoneAsync(childPid, childIdentity, TimeSpan.FromSeconds(5));
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>Reads PTY output until the leader reports its backgrounded child as
    /// <c>CHILD:{pid}:DONE</c>. The trailing marker matters: without it a chunk boundary could
    /// split the digits and a prefix of the pid would parse as a (wrong, possibly live) pid.</summary>
    static async Task<int> ReadReportedChildPidAsync(Capacitor.Cli.Daemon.Pty.IPtyProcess proc) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new System.Text.StringBuilder();

        await foreach (var chunk in proc.ReadOutputAsync(cts.Token)) {
            buffer.Append(System.Text.Encoding.UTF8.GetString(chunk));
            var match = System.Text.RegularExpressions.Regex.Match(buffer.ToString(), @"CHILD:(\d+):DONE");
            if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return -1;
    }
}
