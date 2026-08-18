using System.Diagnostics;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// <see cref="AgyTurnProcess"/> against a REAL child — a trivial sleeper, not <c>agy</c>, so this is
/// ungated and runs everywhere POSIX. What it pins is the process-lifecycle contract the runtime's
/// confirmed-exit gate is built on, and that contract cannot be observed through a fake: the whole
/// question is whether a KILL has actually landed by the time disposal returns, which only a real
/// process table can answer.
/// </summary>
public class AgyTurnProcessTests {
    /// <summary>
    /// Disposal of a still-running child KILLS it. Exec-per-turn means one child per review round, so
    /// a disposal that only released handles would leak a live <c>agy</c> (and its MCP stdio children)
    /// per round for the life of the daemon.
    ///
    /// <para>Observed through a SECOND handle on the same pid, opened before disposal: the handle
    /// <see cref="AgyTurnProcess"/> owns is disposed by the call under test, so asking it afterwards
    /// would answer from a disposed object rather than from the OS.</para>
    ///
    /// <para><b>What this deliberately does NOT claim</b> is that disposal WAITS for the kill to
    /// land. It cannot: <c>Kill(entireProcessTree: true)</c> is SIGKILL on POSIX, which a child can
    /// neither catch nor defer, so an added bounded wait was measured to be unobservable — the mutant
    /// that deletes it survived 6 of 6 runs of this very test. Exit CONFIRMATION is therefore not
    /// disposal's job at all; it belongs to the caller, before it lets go of the handle.</para>
    /// </summary>
    [Test]
    public async Task Disposing_a_running_turn_child_kills_it() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "Uses /bin/sh as a long-lived child; the Antigravity reviewer is POSIX-only anyway.");

        var child = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 300\"") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        })!;

        using var observer = Process.GetProcessById(child.Id);
        var turn = new AgyTurnProcess(child, NullLogger<AgyTurnProcess>.Instance);

        // The precondition, asserted rather than assumed: without a child that is genuinely still
        // running, disposal has nothing to settle and this test proves nothing.
        await Assert.That(turn.HasExited).IsFalse();

        await turn.DisposeAsync();

        // Bounded WAIT, not an immediate read. SIGKILL is delivered asynchronously — the child
        // transitions to zombie some scheduling moment after Kill() returns, and disposal does not
        // block for that — so asserting HasExited on the very next line is a race that a loaded CI
        // box can lose. It passed 14 consecutive local runs, which is exactly the sample size that
        // makes a load-dependent flake look solved; this branch has already been bitten twice by
        // that class. Waiting costs nothing on the happy path and removes the race outright.
        //
        // Waited on the OBSERVER, never on `child`: `child` is the handle AgyTurnProcess just
        // disposed, and touching a disposed Process throws.
        observer.WaitForExit(milliseconds: 5000);

        await Assert.That(observer.HasExited).IsTrue()
            .Because("a disposal that only releases handles leaks one live agy child per review round");
    }
}
