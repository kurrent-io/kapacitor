using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// End-to-end wiring of the consent-dialog fail-fast into the orchestrator's PTY read loop: an
/// unattended review-flow reviewer that renders the Bypass-Permissions consent dialog must
/// (1) fail the launch fast with the actionable coded reason (instead of dying silently at the
/// server's session-id timeout), (2) terminate the wedged-but-alive process, and (3) leave a
/// retained tail-of-PTY capture on disk. Also proves an interactive (Default) agent is NOT
/// failed-fast on the same output — its human viewer can dismiss the prompt.
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its BuildOrchestrator /
/// CaptureServerConnection / SpyPtyProcessFactory harness.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    // The real Claude 2.1.x banner, condensed to the two matched markers.
    const string BypassDialogOutput =
        "\x1b[2J\x1b[H WARNING: Claude Code running in Bypass Permissions mode \n" +
        " In Bypass Permissions mode, Claude Code will not ask for your approval.\n" +
        " 1. No, exit\n 2. Yes, I accept\n";

    [Test]
    public async Task ReviewFlow_reviewer_wedged_on_bypass_dialog_fails_fast_and_captures_the_tail() {
        var server = new CaptureServerConnection();

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var pty   = new BypassBannerPtyProcess(BypassDialogOutput);
        var agent = orch.SeedAgentForTest("rev-wedge", LaunchKind.ReviewFlow, status: "Starting", pty: pty);

        await orch.ReadAgentOutputForTest(agent).WaitAsync(TimeSpan.FromSeconds(10));

        // (1) Actionable fail-fast, not a silent session-id timeout.
        var failed = server.LaunchFailedCalls.SingleOrDefault(c => c.AgentId == "rev-wedge");
        await Assert.That(failed.Reason).IsNotNull();
        await Assert.That(failed.Reason).Contains("Bypass-Permissions");
        await Assert.That(server.StatusChangedCalls).Contains(("rev-wedge", "Failed"));

        // (2) The wedged (still-alive) process was terminated.
        await Assert.That(pty.Terminated).IsTrue();

        // (3) The PTY tail is on disk for post-mortem, containing the TRIGGERING banner chunk — not
        // just the coded reason header. The detector inspects a chunk before the read loop appends it
        // to OutputBuffer, so a banner delivered in the first chunk (this shape) must still be captured.
        // Assert on banner-only text ("will not ask for your approval") that the reason string does
        // NOT contain, so this can't pass merely because the reason header quotes "Yes, I accept".
        var failedDir = orch.FailedLaunchLogForTest!.Dir;
        var logs = Directory.Exists(failedDir) ? Directory.GetFiles(failedDir, "*.log") : [];
        await Assert.That(logs.Length).IsEqualTo(1);
        var captured = await File.ReadAllTextAsync(logs[0]);
        await Assert.That(captured).Contains("will not ask for your approval");
    }

    [Test]
    public async Task ReviewFlow_reviewer_with_a_live_session_is_not_failed_fast_on_banner_like_output() {
        var server = new CaptureServerConnection();

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // The consent/trust dialog is a PRE-SESSION event: it renders once at startup, before any
        // session exists. Once the session is live (SessionId resolved from the transcript) the dialog
        // phase is over, so the detector must stop scanning — otherwise ordinary reviewer/tool output
        // that merely quotes a banner phrase would latch a false wedge and kill a healthy reviewer.
        var pty   = new BypassBannerPtyProcess(BypassDialogOutput, exitAfterBanner: true);
        var agent = orch.SeedAgentForTest("rev-live", LaunchKind.ReviewFlow, status: "Starting", pty: pty);
        agent.SessionId = "live-session-abc";

        await orch.ReadAgentOutputForTest(agent).WaitAsync(TimeSpan.FromSeconds(10));

        // No consent-dialog fail-fast fired: the wedged-detector never terminated the process, and no
        // Bypass-Permissions coded reason was reported (a startup-failure classification of the raw
        // banner text is a different, non-hyphenated reason and is not asserted here).
        await Assert.That(pty.Terminated).IsFalse();
        await Assert.That(
            server.LaunchFailedCalls.Any(c => c.AgentId == "rev-live" && c.Reason.Contains("Bypass-Permissions")))
            .IsFalse();
    }

    [Test]
    public async Task Interactive_agent_is_not_failed_fast_on_the_same_dialog_output() {
        var server = new CaptureServerConnection();

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // Default (interactive) kind: a human viewer can accept the dialog, so no fail-fast.
        // The PTY exits on its own after the banner so the read loop terminates for the test.
        var pty   = new BypassBannerPtyProcess(BypassDialogOutput, exitAfterBanner: true);
        var agent = orch.SeedAgentForTest("interactive", LaunchKind.Default, status: "Starting", pty: pty);

        await orch.ReadAgentOutputForTest(agent).WaitAsync(TimeSpan.FromSeconds(10));

        // No consent-dialog fail-fast fired (the process was never force-terminated by the detector).
        await Assert.That(pty.Terminated).IsFalse();
        await Assert.That(server.LaunchFailedCalls.Any(c => c.Reason.Contains("Bypass-Permissions"))).IsFalse();
    }

    /// <summary>Emits a scripted banner chunk then either blocks alive (a real wedge — the detector
    /// must terminate it) or exits (interactive control case). Records whether TerminateAsync ran.</summary>
    sealed class BypassBannerPtyProcess(string banner, bool exitAfterBanner = false) : IPtyProcess {
        volatile bool _exited;
        public int  Pid       => 5150;
        public bool HasExited => _exited || exitAfterBanner;
        public int? ExitCode  => HasExited ? 0 : null;
        public bool Terminated { get; private set; }

        public ValueTask DisposeAsync() { _exited = true; return default; }
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? _) {
            Terminated = true;
            _exited    = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield return Encoding.UTF8.GetBytes(banner);

            if (!exitAfterBanner) {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { /* stopped */ }
            }

            yield break;
        }

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }
}
