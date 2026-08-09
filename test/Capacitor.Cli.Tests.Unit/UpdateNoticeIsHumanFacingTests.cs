using Capacitor.Cli;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit coverage for <see cref="UpdateNotice.IsHumanFacing"/> — the sync, I/O-free half of the
/// suppression predicate (the async half, <c>profile.UpdateCheck == false</c>, is covered by
/// <c>UpdateNoticeDeliveryTests</c> in the integration suite since it needs a real profile
/// config file on disk). Asserts both directions of the matrix non-vacuously: every suppressed
/// case is paired with the fact that ordinary human-facing commands are NOT suppressed, so a
/// predicate hard-coded to <c>true</c> or <c>false</c> would fail here.
/// </summary>
public class UpdateNoticeIsHumanFacingTests {
    static readonly string[] NoArgs = [];

    // --- Suppressed: CrashReporter.FailOpenCommands (agent-spawned; nobody reads their stderr) ---

    [Test]
    [Arguments("hook")]
    [Arguments("generate-whats-done")]
    [Arguments("set-title")]
    [Arguments("copilot-finalize")]
    public async Task FailOpenCommands_AreSuppressed(string command) {
        await Assert.That(UpdateNotice.IsHumanFacing(command, [command])).IsFalse();
    }

    // --- Suppressed: mcp / watch (stdio server / long-lived background process) ---

    [Test]
    public async Task Mcp_IsSuppressed_RegardlessOfSubcommand() {
        await Assert.That(UpdateNotice.IsHumanFacing("mcp", ["mcp"])).IsFalse();
        await Assert.That(UpdateNotice.IsHumanFacing("mcp", ["mcp", "flows"])).IsFalse();
        await Assert.That(UpdateNotice.IsHumanFacing("mcp", ["mcp", "sessions"])).IsFalse();
    }

    [Test]
    public async Task Watch_IsSuppressed() {
        await Assert.That(UpdateNotice.IsHumanFacing("watch", ["watch", "sid", "/tmp/t.jsonl"])).IsFalse();
    }

    // --- Suppressed: `daemon run` specifically — NOT every daemon subcommand ---

    [Test]
    public async Task DaemonRun_IsSuppressed() {
        await Assert.That(UpdateNotice.IsHumanFacing("daemon", ["daemon", "run"])).IsFalse();
    }

    [Test]
    [Arguments("status")]
    [Arguments("stop")]
    [Arguments("install")]
    public async Task DaemonOtherSubcommands_AreNotSuppressed(string subcommand) {
        // Only `daemon run` (the foreground daemon process itself) is excluded — a bare `daemon`
        // with no subcommand, or any other subcommand, is an ordinary human-facing CLI call.
        await Assert.That(UpdateNotice.IsHumanFacing("daemon", ["daemon", subcommand])).IsTrue();
    }

    [Test]
    public async Task BareDaemon_WithNoSubcommand_IsNotSuppressed() {
        await Assert.That(UpdateNotice.IsHumanFacing("daemon", ["daemon"])).IsTrue();
    }

    // --- Suppressed: update / uninstall themselves ---

    [Test]
    public async Task Update_IsSuppressed() {
        await Assert.That(UpdateNotice.IsHumanFacing("update", ["update"])).IsFalse();
    }

    [Test]
    public async Task Uninstall_IsSuppressed() {
        await Assert.That(UpdateNotice.IsHumanFacing("uninstall", ["uninstall"])).IsFalse();
    }

    // --- Suppressed: explicit --no-update-check flag, on an otherwise human-facing command ---

    [Test]
    public async Task NoUpdateCheckFlag_SuppressesAnOtherwiseHumanFacingCommand() {
        await Assert.That(UpdateNotice.IsHumanFacing("config", ["config", "show", "--no-update-check"])).IsFalse();
    }

    // --- NOT suppressed: ordinary human-facing commands (the positive control) ---

    [Test]
    [Arguments("status")]
    [Arguments("config")]
    [Arguments("whoami")]
    [Arguments("recap")]
    [Arguments("errors")]
    [Arguments("eval")]
    [Arguments("import")]
    [Arguments("--help")]
    [Arguments("-h")]
    [Arguments("help")]
    [Arguments("--version")]
    [Arguments("unknown-command")]
    public async Task OrdinaryCommands_AreHumanFacing(string command) {
        await Assert.That(UpdateNotice.IsHumanFacing(command, [command])).IsTrue();
    }

    [Test]
    public async Task EmptyArgs_DoesNotThrow_AndIsHumanFacing() {
        // args always contains at least the command itself in production (args[0] == command),
        // but the predicate must not blow up on a caller that passes an empty array.
        await Assert.That(UpdateNotice.IsHumanFacing("status", NoArgs)).IsTrue();
    }
}
