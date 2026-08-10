// test/Capacitor.Cli.Tests.Unit/Services/PiRpcProcessTests.cs
using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// <see cref="PiRpcProcess"/> against a REAL child — <c>/bin/cat</c>, which echoes each stdin line
/// back on stdout, so it doubles as a stand-in for Pi's stdio JSONL RPC without depending on the
/// real binary being installed. Unlike <c>AgyTurnProcess</c> (one process per turn, stdin closed at
/// spawn), a Pi RPC child is LONG-LIVED and its stdin stays open for the whole session — these tests
/// pin exactly that difference, plus the lifecycle contracts shared with every other real-process
/// wrapper in this daemon (idempotent dispose, terminate-after-dispose, confirmed exit after kill).
/// </summary>
public class PiRpcProcessTests {
    static Process StartCat() =>
        Process.Start(new ProcessStartInfo("/bin/cat") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        })!;

    /// <summary>A child that NEVER reads its stdin — unlike <c>/bin/cat</c>, which drains stdin as
    /// fast as it's written, so a write to it can never fill the OS pipe buffer and block. Used only
    /// by the dispose/write race test below, which needs a writer to be genuinely stuck mid-flush,
    /// holding the stdin gate, for the race it exercises to be real rather than incidental.</summary>
    static Process StartNonReadingChild() =>
        Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exec sleep 30\"") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        })!;

    static async Task<Exception?> SwallowFault(Task task) {
        try {
            await task.ConfigureAwait(false);
            return null;
        } catch (Exception ex) {
            return ex;
        }
    }

    /// <summary>
    /// The whole point of this abstraction over <c>AgyTurnProcess</c>: stdin is NOT closed at
    /// construction, so a line written after construction is still deliverable — and because the
    /// child is <c>cat</c>, the same line comes back out on stdout.
    /// </summary>
    [Test]
    public async Task Written_line_is_echoed_back_through_ReadLinesAsync() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        await using var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        await proc.WriteLineAsync("""{"type":"prompt","id":"req-1"}""", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = proc.ReadLinesAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current).IsEqualTo("""{"type":"prompt","id":"req-1"}""");
    }

    /// <summary>
    /// Two callers writing concurrently must never interleave — a torn line is unparseable JSON on
    /// the other end. <c>WriteLineAsync</c> serializes writers under a semaphore, so both full lines
    /// must arrive intact (order between them is unspecified; interleaving is not).
    /// </summary>
    [Test]
    public async Task Concurrent_writers_never_interleave_partial_lines() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        await using var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        var lineA = new string('a', 20_000);
        var lineB = new string('b', 20_000);

        var writeA = proc.WriteLineAsync(lineA, CancellationToken.None);
        var writeB = proc.WriteLineAsync(lineB, CancellationToken.None);
        await Task.WhenAll(writeA, writeB);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = proc.ReadLinesAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        var first = enumerator.Current;

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        var second = enumerator.Current;

        var received = new[] { first, second }.OrderBy((string s) => s, StringComparer.Ordinal).ToArray();
        var expected = new[] { lineA, lineB }.OrderBy((string s) => s, StringComparer.Ordinal).ToArray();

        await Assert.That(received).IsEquivalentTo(expected);
    }

    /// <summary><see cref="IAsyncDisposable.DisposeAsync"/> must be idempotent — a second call on the
    /// same instance is a safe no-op, never a throw.</summary>
    [Test]
    public async Task DisposeAsync_is_idempotent() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        await proc.DisposeAsync();
        await proc.DisposeAsync();
    }

    /// <summary><see cref="PiRpcProcess.TerminateAsync"/> must be safe to call AFTER
    /// <see cref="IAsyncDisposable.DisposeAsync"/> has already run.</summary>
    [Test]
    public async Task TerminateAsync_is_safe_after_dispose() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        await proc.DisposeAsync();
        await proc.TerminateAsync();
    }

    /// <summary>Terminating a running child kills it: <see cref="PiRpcProcess.HasExited"/> flips true
    /// and <see cref="PiRpcProcess.ExitCode"/> becomes observable once the OS confirms the exit.</summary>
    [Test]
    public async Task Terminate_kills_a_running_child_and_sets_HasExited_and_ExitCode() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        var child = StartCat();
        using var observer = Process.GetProcessById(child.Id);
        var proc = new PiRpcProcess(child, NullLogger<PiRpcProcess>.Instance);

        await Assert.That(proc.HasExited).IsFalse();

        await proc.TerminateAsync();

        await Assert.That(proc.HasExited).IsTrue();
        await Assert.That(proc.ExitCode).IsNotNull();

        observer.WaitForExit(milliseconds: 5000);
        await Assert.That(observer.HasExited).IsTrue();

        await proc.DisposeAsync();
    }

    /// <summary><see cref="PiRpcProcess.WaitForExitAsync"/> returns once a killed child's exit is
    /// confirmed by the OS, rather than hanging or returning only on its own timeout.</summary>
    [Test]
    public async Task WaitForExitAsync_returns_after_kill() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        await proc.TerminateAsync();
        await proc.WaitForExitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(proc.HasExited).IsTrue();

        await proc.DisposeAsync();
    }

    /// <summary>
    /// The regression this pins: <c>DisposeAsync</c> used to call <c>_stdinGate.Dispose()</c>, but
    /// <see cref="SemaphoreSlim.Dispose()"/> does not release outstanding async waiters. That only
    /// bites when a SECOND writer is genuinely queued in <c>WaitAsync</c> behind a first writer that
    /// is itself still blocked holding the gate — a single unopposed <c>WriteLineAsync</c> acquires
    /// the semaphore synchronously and never exercises the disposed-waiter path at all (an earlier
    /// version of this test raced exactly one write against dispose and was proven vacuous by
    /// mutation testing: reverting the fix left it green).
    ///
    /// <para>So this test manufactures the real precondition directly: A child that never reads its
    /// stdin (<see cref="StartNonReadingChild"/>) lets writer A block mid-<c>WriteAsync</c>/
    /// <c>FlushAsync</c> — <c>Process.StandardInput</c>'s <c>AutoFlush</c> is on by default, so
    /// writing more than the OS pipe buffer can hold with nobody draining it blocks the write itself
    /// — WHILE HOLDING <c>_stdinGate</c>. Writer B then queues behind A in <c>WaitAsync</c>. Only
    /// with a real waiter parked on the gate does disposing it (the bug) or leaving it alone (the
    /// fix) diverge. <c>DisposeAsync</c> then runs concurrently with both; the pre-fix code hangs B
    /// forever, the fix lets everything settle (faulted is fine — the kill breaks A's pipe and B's
    /// disposed-check throws) within the bounded wait below.</para>
    /// </summary>
    [Test]
    public async Task DisposeAsync_racing_a_writer_queued_behind_a_blocked_write_never_hangs() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/sh as a real long-lived, non-reading child; Pi hosting is POSIX-only anyway.");

        var proc = new PiRpcProcess(StartNonReadingChild(), NullLogger<PiRpcProcess>.Instance);

        // Bigger than any OS pipe buffer this test could plausibly run against (typically 16-64KB
        // on macOS/Linux) — large enough that A is still mid-flush, holding the gate, when B tries
        // to queue behind it.
        var hugeLine = new string('a', 8 * 1024 * 1024);
        var writeA   = SwallowFault(proc.WriteLineAsync(hugeLine, CancellationToken.None));

        // Give A time to acquire the gate and actually block on the pipe write, rather than racing
        // its own semaphore acquisition.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await Assert.That(writeA.IsCompleted).IsFalse()
            .Because("A must still be blocked mid-write, holding the gate, for B to queue behind it " +
                      "— if A already finished (e.g. the OS pipe buffer absorbed the whole line) this " +
                      "test proves nothing, exactly like the mutation-caught prior version");

        // B queues in WaitAsync behind A. THIS is the state the pre-fix `_stdinGate.Dispose()` could
        // strand forever.
        var writeB = SwallowFault(proc.WriteLineAsync("""{"type":"prompt","id":"queued"}""", CancellationToken.None));

        var disposeTask = proc.DisposeAsync().AsTask();

        var all    = Task.WhenAll(writeA, writeB, disposeTask);
        var winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.That(ReferenceEquals(winner, all)).IsTrue()
            .Because("neither the blocked writer, the writer queued behind it, nor dispose itself " +
                      "may hang — the defect under test was disposing the stdin semaphore while a " +
                      "waiter was queued on it");
    }
}
