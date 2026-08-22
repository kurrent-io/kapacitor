using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// `kcap agent …` must reach <c>AgentCommand</c>. Pinned through the real binary because the
/// dispatch lives in Program.cs's top-level flow, where a guard added ahead of the switch can
/// shadow the whole group without failing a single unit test — which is exactly what happened
/// when the retired-verb tombstone and this command group landed in the same release.
///
/// Every case asserts a string only <c>AgentCommand</c> emits. Asserting merely "not the
/// tombstone" would pass for any other pre-dispatch guard that exits 1 (the missing-server gate
/// did exactly that), which would leave this file green while the group stayed shadowed.
/// </summary>
public class AgentVerbDispatchTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    /// Emitted by AgentCommand and nothing else: the Unix subcommand paths all prefix their
    /// usage/errors with `kcap agent`, and Windows refuses the group with the same prefix.
    const string HandlerMarker = "kcap agent";

    [Test]
    [Arguments("agent frobnicate")]
    [Arguments("agent attach")]
    [Arguments("agent stop")]
    [Arguments("agent start")]
    public async Task Subcommand_reaches_the_handler(string argLine) {
        var (stdout, stderr, exitCode) = await RunCli(argLine);
        var output = stdout + stderr;

        await Assert.That(output).Contains(HandlerMarker);
        await Assert.That(output).DoesNotContain("renamed to 'daemon'");
        await Assert.That(output).DoesNotContain("No server configured");
        await Assert.That(exitCode).IsNotEqualTo(2);
    }

    [Test]
    [Arguments("agent --daemon kcap-dispatch-test-absent")]
    [Arguments("agent ls --daemon kcap-dispatch-test-absent")]
    public async Task Bare_agent_and_ls_reach_the_handler(string argLine) {
        // Naming a daemon that cannot exist makes the handler's own "no daemon" message
        // deterministic regardless of what is running on the machine. The bare form also pins
        // that a leading flag is an `ls` option rather than a subcommand named `--daemon`.
        var (stdout, stderr, _) = await RunCli(argLine);
        var output = stdout + stderr;

        await Assert.That(output).DoesNotContain("renamed to 'daemon'");
        await Assert.That(output).DoesNotContain("No server configured");
        await Assert.That(output).DoesNotContain("unknown subcommand");

        if (!OperatingSystem.IsWindows()) {
            await Assert.That(output).Contains("No local daemon running.");
        } else {
            await Assert.That(output).Contains(HandlerMarker);
        }
    }

    [Test]
    public async Task Agent_help_renders_the_command_group() {
        var (stdout, _, exitCode) = await RunCli("agent --help");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("kcap agent");

        foreach (var sub in new[] { "start", "ls", "stop", "attach" }) {
            await Assert.That(stdout).Contains(sub);
        }
    }

    [Test]
    public async Task Daemon_only_subcommand_points_at_the_daemon_group() {
        // `status` only ever meant the daemon. Signposted ahead of the platform guard, so this
        // holds on Windows too — where `kcap daemon status` is supported but this group is not.
        var (_, stderr, exitCode) = await RunCli("agent status");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("kcap daemon status");
        await Assert.That(stderr).DoesNotContain("not supported on Windows");
    }

    [Test]
    public async Task Start_without_a_server_reports_the_missing_server_itself() {
        // The group is offline-callable, so the server requirement belongs to `start` alone and
        // must still be reported — just by the handler, not by the global gate.
        if (OperatingSystem.IsWindows()) return;

        var (_, stderr, exitCode) = await RunCli("agent start claude", clearServerUrl: true);

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("kcap agent start: no server configured");
    }

    [Test]
    public async Task Stop_accepts_the_force_flag_without_treating_it_as_an_id() {
        // --force must parse as a flag, not as a positional agent id, or the usage line fires.
        var (_, stderr, _) = await RunCli("agent stop --all --force -y --daemon kcap-dispatch-test-absent");

        await Assert.That(stderr).DoesNotContain("cannot combine an agent id with --all");
        await Assert.That(stderr).DoesNotContain("usage: kcap agent stop");
    }

    [Test]
    public async Task Stop_force_alone_without_all_or_an_id_still_reports_usage() {
        // Neither --all nor a positional id is present, so the usage line must fire. If --force
        // were ever mistreated as the positional agent id, hasId would flip true here and this
        // would instead try to resolve "--force" as a target — the previous test above can't
        // catch that regression because its args[0] is "--all", which already forces hasId false
        // regardless of where --force sits.
        var (_, stderr, exitCode) = await RunCli("agent stop --force --daemon kcap-dispatch-test-absent");

        await Assert.That(stderr).Contains("usage: kcap agent stop");
        await Assert.That(exitCode).IsEqualTo(1);
    }

    async Task<(string Stdout, string Stderr, int ExitCode)> RunCli(
            string argLine, bool clearServerUrl = false) {

        var psi = KcapProcess.StartInfo(Daemons.Store);
        // A string, not ArgumentList: quote-aware parsing, so an argument may contain a space.
        psi.Arguments = $"{argLine} --no-update-check";

        // Isolate the config surface to a FRESH dir. Clearing KCAP_URL alone is not enough: the CLI
        // resolves the server from a persisted profile too, and the child inherits the assembly's
        // SHARED KCAP_CONFIG_DIR (IntegrationGlobalSetup). A sibling integration test can write a
        // server_url into that shared config, which would defeat clearServerUrl and stop
        // "no server configured" from firing on a loaded runner. A per-call config dir makes the
        // test-controlled KCAP_URL the only server signal.
        using var configDir = new TempDir();
        psi.Environment["KCAP_CONFIG_DIR"] = configDir.Path;
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
