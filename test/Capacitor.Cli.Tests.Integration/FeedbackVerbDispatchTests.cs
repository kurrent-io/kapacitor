using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// `kcap feedback` must reach <see cref="Capacitor.Cli.Commands.FeedbackCommand"/>. Pinned through
/// the real binary for the same reason <c>AgentVerbDispatchTests</c> is: the dispatch lives in
/// Program.cs's top-level flow, where a guard added ahead of the switch can shadow a whole verb
/// without failing a single unit test that only calls <c>FeedbackCommand</c> directly.
///
/// Asserts a string only <c>FeedbackCommand</c> emits (its usage line naming both category flags)
/// rather than merely "not Unknown command" — a bare "not the tombstone" check would pass for any
/// other pre-dispatch guard that exits non-zero with unrelated text.
/// </summary>
public class FeedbackVerbDispatchTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Bare_feedback_reaches_the_handlers_usage_error() {
        var (stdout, stderr, exitCode) = await RunCli("feedback");
        var output = stdout + stderr;

        await Assert.That(output).Contains("kcap feedback (--bug | --feedback)");
        await Assert.That(output).Contains("Pass exactly one of --bug or --feedback.");
        await Assert.That(output).DoesNotContain("Unknown command");
        await Assert.That(exitCode).IsNotEqualTo(0);
    }

    async Task<(string Stdout, string Stderr, int ExitCode)> RunCli(
            string argLine, bool clearServerUrl = false) {

        var psi = KcapProcess.StartInfo(Daemons.Store);
        // A string, not ArgumentList: quote-aware parsing, so an argument may contain a space.
        psi.Arguments = $"{argLine} --no-update-check";

        // Isolate from the developer's own profile so this assertion doesn't depend on whether
        // this machine happens to have a server configured. Unreachable-but-present is enough:
        // FeedbackCommand's usage error fires before any server call is attempted.
        psi.Environment["KCAP_URL"] = clearServerUrl ? "" : "http://127.0.0.1:1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kcap process");

        // Nothing writes to the child; leaving the pipe open would hang any prompt it reaches.
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

}
