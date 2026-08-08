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
    /// <see cref="SemaphoreSlim.Dispose()"/> does not release outstanding async waiters — a
    /// <see cref="PiRpcProcess.WriteLineAsync"/> call blocked in <c>WaitAsync</c> when dispose ran
    /// could hang FOREVER, and the racing writer's own <c>finally</c> release could throw
    /// <see cref="ObjectDisposedException"/> over whatever it was already unwinding. Task 3's runtime
    /// races exactly this shape — a send or abort racing teardown — so this test drives the real
    /// race rather than just unit-testing the fixed code in isolation: a write in flight when
    /// dispose runs, and a write issued strictly after dispose has completed, must both settle
    /// promptly, never hang.
    /// </summary>
    [Test]
    public async Task DisposeAsync_racing_a_WriteLineAsync_never_hangs_and_leaves_writes_failing_fast() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Uses /bin/cat as a real long-lived RPC-shaped child; Pi hosting is POSIX-only anyway.");

        var proc = new PiRpcProcess(StartCat(), NullLogger<PiRpcProcess>.Instance);

        // Race a write against dispose. This half of the test does not care whether the racing
        // write succeeds (it may complete before the kill lands) or faults (if the kill wins) — the
        // only claim it makes is that NEITHER task hangs waiting on the other, which is exactly what
        // a disposed-out-from-under-it SemaphoreSlim would cause.
        var racingWrite = proc.WriteLineAsync("""{"type":"prompt","id":"racing"}""", CancellationToken.None)
            .ContinueWith(t => t.Exception, TaskContinuationOptions.ExecuteSynchronously);
        var disposeTask = proc.DisposeAsync().AsTask();

        var both   = Task.WhenAll(racingWrite, disposeTask);
        var winner = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.That(ReferenceEquals(winner, both)).IsTrue()
            .Because("a write racing dispose must never hang — the defect under test was disposing " +
                     "the stdin semaphore while a waiter was still queued on it");

        // A write issued strictly AFTER dispose has already completed must fault immediately —
        // never queue behind a gate that will never again be released.
        var postDisposeWrite = proc.WriteLineAsync("""{"type":"prompt","id":"after-dispose"}""", CancellationToken.None);
        var postWinner        = await Task.WhenAny(postDisposeWrite, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.That(ReferenceEquals(postWinner, postDisposeWrite)).IsTrue()
            .Because("a write issued after dispose must fault promptly, not hang");

        Exception? caught = null;

        try {
            await postDisposeWrite;
        } catch (Exception ex) {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught is ObjectDisposedException or IOException).IsTrue()
            .Because($"expected ObjectDisposedException or IOException, got {caught?.GetType().FullName}");
    }
}
