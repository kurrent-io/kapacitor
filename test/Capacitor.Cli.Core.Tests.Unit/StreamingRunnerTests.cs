using System.Diagnostics;
using System.Globalization;

namespace Capacitor.Cli.Core.Tests.Unit;

/// Drives the REAL ProcessRunner's RunStreamingAsync (real child processes) — same rationale as ProcessRunnerTests.
public class StreamingRunnerTests {
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static bool IsAlive(int pid) {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_tags_interleaved_lines_with_the_right_stream_kind() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new ProcessRunner();
        var lines = new List<StreamedLine>();
        var gate = new object();

        var result = await runner.RunStreamingAsync(
            "/bin/sh", ["-c", "echo out-1; echo err-1 >&2; echo out-2; echo err-2 >&2"],
            new RunOptions(), line => { lock (gate) lines.Add(line); }, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(lines.Count(l => l is { Kind: ProcessStreamKind.Stdout, Text: "out-1" })).IsEqualTo(1);
        await Assert.That(lines.Count(l => l is { Kind: ProcessStreamKind.Stdout, Text: "out-2" })).IsEqualTo(1);
        await Assert.That(lines.Count(l => l is { Kind: ProcessStreamKind.Stderr, Text: "err-1" })).IsEqualTo(1);
        await Assert.That(lines.Count(l => l is { Kind: ProcessStreamKind.Stderr, Text: "err-2" })).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_more_than_TailLimit_lines_callback_sees_all_tail_holds_last_500() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new ProcessRunner();
        var callbackCount = 0;

        var result = await runner.RunStreamingAsync(
            "/bin/sh", ["-c", "i=1; while [ $i -le 600 ]; do echo line-$i; i=$((i+1)); done"],
            new RunOptions(), _ => Interlocked.Increment(ref callbackCount), CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(callbackCount).IsEqualTo(600);
        await Assert.That(result.Tail.Count).IsEqualTo(500);
        await Assert.That(result.Tail[0].Text).IsEqualTo("line-101");
        await Assert.That(result.Tail[^1].Text).IsEqualTo("line-600");
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_throwing_callback_does_not_kill_the_pump_and_exit_code_is_captured() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new ProcessRunner();
        var seen = new List<string>();
        var gate = new object();

        var result = await runner.RunStreamingAsync(
            "/bin/sh", ["-c", "echo one; echo two; echo three; exit 7"], new RunOptions(),
            line => {
                if (line.Text == "two") throw new InvalidOperationException("boom");
                lock (gate) seen.Add(line.Text);
            }, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(7);
        await Assert.That(seen).Contains("one");
        await Assert.That(seen).Contains("three");
        // "two" still lands in the tail — only the callback invocation is swallowed.
        await Assert.That(result.Tail.Any(l => l.Text == "two")).IsTrue();
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_ct_cancel_kills_the_tree_and_leaves_no_orphan() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        using var tmp = new TempDir();
        var runner = new ProcessRunner();
        var startedMarker = tmp.PathTo("marker");
        using var cts = new CancellationTokenSource();
        int grandchildPid = -1;
        try {
            var runTask = runner.RunStreamingAsync(
                "/bin/sh", ["-c", $"sleep 30 & echo $! > {startedMarker}; wait"],
                new RunOptions(), _ => { }, cts.Token);

            await WaitUntilAsync(() => File.Exists(startedMarker), TimeSpan.FromSeconds(5), "the grandchild to start and record its PID");
            grandchildPid = int.Parse((await File.ReadAllTextAsync(startedMarker)).Trim(), CultureInfo.InvariantCulture);
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
            await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(5), "the grandchild to die with the cancelled tree");
        } finally {
            if (grandchildPid > 0) {
                try { Process.GetProcessById(grandchildPid).Kill(); }
                catch (ArgumentException) { /* already gone */ }
            }
        }
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_ct_cancel_ignores_AbandonWait_CancelMode_and_still_kills_the_tree() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        using var tmp = new TempDir();
        var runner = new ProcessRunner();
        var startedMarker = tmp.PathTo("marker");
        using var cts = new CancellationTokenSource();
        int grandchildPid = -1;
        try {
            // AbandonWait would let RunAsync's child survive — streaming must still kill it.
            var runTask = runner.RunStreamingAsync(
                "/bin/sh", ["-c", $"sleep 30 & echo $! > {startedMarker}; wait"],
                new RunOptions(CancelMode: CancelMode.AbandonWait), _ => { }, cts.Token);

            await WaitUntilAsync(() => File.Exists(startedMarker), TimeSpan.FromSeconds(5), "the grandchild to start and record its PID");
            grandchildPid = int.Parse((await File.ReadAllTextAsync(startedMarker)).Trim(), CultureInfo.InvariantCulture);
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
            await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(5), "the grandchild to die despite AbandonWait");
        } finally {
            if (grandchildPid > 0) {
                try { Process.GetProcessById(grandchildPid).Kill(); }
                catch (ArgumentException) { /* already gone */ }
            }
        }
    }

    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_timeout_kills_the_tree_and_reports_TimedOut() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new ProcessRunner();
        var sw = Stopwatch.StartNew();

        var result = await runner.RunStreamingAsync(
            "/bin/sleep", ["30"], new RunOptions(Timeout: TimeSpan.FromMilliseconds(200)), _ => { }, CancellationToken.None);
        sw.Stop();

        await Assert.That(result.TimedOut).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    // The ct-cancel branch must await both pumps before throwing — a late onLine after OCE races caller cleanup.
    [Test]
    [NotInParallel("StreamingProcessRunner")]
    public async Task RunStreamingAsync_ct_cancel_awaits_the_pumps_before_throwing_no_late_callbacks() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        var callbackCount = 0;

        var runTask = runner.RunStreamingAsync(
            "/bin/sh", ["-c", "while true; do echo tick; sleep 0.01; done"],
            new RunOptions(), _ => Interlocked.Increment(ref callbackCount), cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref callbackCount) > 0, TimeSpan.FromSeconds(5), "the first line to arrive");
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
        // By the time RunStreamingAsync has thrown, the fix already awaited both pumps to EOF —
        // so no callback can fire after this snapshot (the race the old fire-and-forget allowed).
        var afterThrow = Volatile.Read(ref callbackCount);
        await Task.Delay(250);

        await Assert.That(Volatile.Read(ref callbackCount)).IsEqualTo(afterThrow);
    }

    [Test]
    public async Task StreamingResult_shape_has_no_full_capture_property() {
        var properties = typeof(StreamingResult).GetProperties().Select(p => p.Name).ToArray();

        await Assert.That(properties).Contains(nameof(StreamingResult.ExitCode));
        await Assert.That(properties).Contains(nameof(StreamingResult.TimedOut));
        await Assert.That(properties).Contains(nameof(StreamingResult.Tail));
        await Assert.That(properties.Length).IsEqualTo(3); // no Stdout/Stderr full-capture property, by shape
    }
}
