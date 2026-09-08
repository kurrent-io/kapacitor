using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudeLauncherPermissionModeTests {
    [TempHome] public required TempHome Home { get; init; }

    ClaudeLauncher NewLauncher() =>
        new(new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" }, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);

    static LauncherContext NewCtx(bool isReviewFlow, string? permissionMode) =>
        new(
            AgentId:       "a-1",
            SourceRepoPath:"/tmp/repo",
            Worktree:      new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt:        "build it",
            Model:         "sonnet",
            Effort:        null,
            Tools:         null,
            IsReview:      false,
            IsReviewFlow:  isReviewFlow,
            Review:        null,
            ReviewLaunch:  null
        ) { PermissionMode = permissionMode };

    static string? ModeArg(string[] args) {
        var i = Array.IndexOf(args, "--permission-mode");
        return i < 0 ? null : args[i + 1];
    }

    [Test]
    public async Task Interactive_launch_forwards_the_selected_mode_verbatim() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: false, "acceptEdits")).Args;

        await Assert.That(ModeArg(args)).IsEqualTo("acceptEdits");
        await Assert.That(args.Count(a => a == "--permission-mode")).IsEqualTo(1);
    }

    [Test]
    public async Task Interactive_launch_without_a_mode_passes_no_flag() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: false, null)).Args;

        await Assert.That(args).DoesNotContain("--permission-mode");
    }

    /// The reviewer's bypass is a containment guarantee: a mode that somehow reaches the launcher
    /// on a review flow (the orchestrator refuses it earlier) must not add a second flag.
    [Test]
    public async Task Review_flow_keeps_its_own_bypass_regardless_of_a_mode() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, "manual")).Args;

        await Assert.That(ModeArg(args)).IsEqualTo("bypassPermissions");
        await Assert.That(args.Count(a => a == "--permission-mode")).IsEqualTo(1);
    }

    /// Bypass turns off permission prompts, not every prompt: a question card or the one-time
    /// bypass consent dialog can still be open, so the composer keeps the single-Enter submit.
    [Test]
    public async Task Bypass_selected_interactively_keeps_the_interactive_submit_strategy() {
        await Assert.That(NewLauncher().DisablesApprovalPrompts(NewCtx(isReviewFlow: false, "bypassPermissions"))).IsFalse();
    }
}
