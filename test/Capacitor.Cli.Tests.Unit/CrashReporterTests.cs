namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// the CLI's top-level guard converts an otherwise-uncaught exception
/// (which NativeAOT turns into a SIGABRT + crash report on a ~1s hook/generator
/// process) into a logged, clean exit. These cover the pure decisions —
/// which commands fail open, the resulting exit code, and the crash-log entry
/// format — that back that guard.
/// </summary>
public class CrashReporterTests {
    [Test]
    [Arguments("hook")]
    [Arguments("generate-whats-done")]
    [Arguments("set-title")]
    [Arguments("copilot-finalize")]
    public async Task IsFailOpenCommand_True_ForHookAndDetachedGenerators(string command) {
        await Assert.That(CrashReporter.IsFailOpenCommand(command)).IsTrue();
    }

    [Test]
    [Arguments("status")]
    [Arguments("login")]
    [Arguments("daemon")]
    [Arguments("import")]
    [Arguments("config")]
    [Arguments(null)]
    public async Task IsFailOpenCommand_False_ForInteractiveOrUnknownCommands(string? command) {
        await Assert.That(CrashReporter.IsFailOpenCommand(command)).IsFalse();
    }

    [Test]
    public async Task ExitCode_IsZeroForFailOpen_OneOtherwise() {
        // Fail-open commands must exit 0 so a crash never surfaces to the agent
        // (no error banner, no crash report) — matching the hook's own fail-open.
        await Assert.That(CrashReporter.ExitCode("hook")).IsEqualTo(0);
        await Assert.That(CrashReporter.ExitCode("generate-whats-done")).IsEqualTo(0);
        // Interactive commands surface the failure (non-zero) but still no abort.
        await Assert.That(CrashReporter.ExitCode("status")).IsEqualTo(1);
        await Assert.That(CrashReporter.ExitCode(null)).IsEqualTo(1);
    }

    [Test]
    public async Task FormatEntry_IncludesTimestamp_Command_And_ExceptionDetail() {
        Exception ex;
        try {
            throw new InvalidOperationException("boom-xyz");
        } catch (Exception caught) {
            ex = caught;
        }

        var now   = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var entry = CrashReporter.FormatEntry("hook", ex, now);

        await Assert.That(entry).Contains("2026-07-03");
        await Assert.That(entry).Contains("command=hook");
        await Assert.That(entry).Contains("InvalidOperationException");
        await Assert.That(entry).Contains("boom-xyz");
    }

    [Test]
    public async Task FormatEntry_ToleratesNullCommand() {
        var entry = CrashReporter.FormatEntry(null, new InvalidOperationException("x"), DateTimeOffset.UtcNow);
        await Assert.That(entry).Contains("command=?");
    }
}
