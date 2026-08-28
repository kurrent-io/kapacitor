using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupDecisionsTests {
    [Test]
    public async Task DecideInstallAgents_NoAgentsDetected_ReturnsFalseWithoutPrompting() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(
            detected, noPrompt: false, promptYesNo: _ => throw new InvalidOperationException("must not prompt"));

        await Assert.That(installAgents).IsFalse();
        await Assert.That(SetupDecisions.DetectedAgentsSummary(detected)).IsNull();
    }

    [Test]
    public async Task DecideInstallAgents_NoPromptTrue_ReturnsTrueWithoutPrompting() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(
            detected, noPrompt: true, promptYesNo: _ => throw new InvalidOperationException("must not prompt"));

        await Assert.That(installAgents).IsTrue();
    }

    [Test]
    public async Task DecideInstallAgents_Interactive_ReturnsPromptResultWhenAccepted() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt: false, promptYesNo: _ => true);

        await Assert.That(installAgents).IsTrue();
    }

    [Test]
    public async Task DecideInstallAgents_Interactive_ReturnsPromptResultWhenDeclined() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: false, Cursor: false, Copilot: false);

        var installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt: false, promptYesNo: _ => false);

        await Assert.That(installAgents).IsFalse();
    }

    [Test]
    public async Task DetectedAgentsSummary_KiroDetected_ContainsDefaultAgentAnnotation() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: false, Codex: false, Cursor: false, Copilot: false, Kiro: true);

        var summary = SetupDecisions.DetectedAgentsSummary(detected);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!).Contains("Kiro (installing sets kcap as your default Kiro agent)");
    }

    [Test]
    public async Task DetectedAgentsSummary_MultipleDetected_CommaJoinsFriendlyNames() {
        var detected = new CodingAgentsStep.DetectedAgents(Claude: true, Codex: true, Cursor: false, Copilot: false);

        var summary = SetupDecisions.DetectedAgentsSummary(detected);

        await Assert.That(summary).IsEqualTo("Claude Code, Codex");
    }

    // --- DecideImport (Step 6 — import past sessions) ---

    [Test]
    public async Task DecideImport_NoCurrentRepo_SkipsWithReason() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: false, authSatisfied: true, skipImport: false, noPrompt: false,
            promptYesNo: () => throw new InvalidOperationException("must not prompt"));

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Skip);
        await Assert.That(decision.SkipReason).IsEqualTo("no origin remote — skipping import");
    }

    [Test]
    public async Task DecideImport_AuthNotSatisfied_SkipsWithReason() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: true, authSatisfied: false, skipImport: false, noPrompt: false,
            promptYesNo: () => throw new InvalidOperationException("must not prompt"));

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Skip);
        await Assert.That(decision.SkipReason).IsEqualTo("not authenticated — skipping import");
    }

    [Test]
    public async Task DecideImport_SkipImportFlag_SkipsWithReason() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: true, authSatisfied: true, skipImport: true, noPrompt: false,
            promptYesNo: () => throw new InvalidOperationException("must not prompt"));

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Skip);
        await Assert.That(decision.SkipReason).IsEqualTo("--skip-import");
    }

    [Test]
    public async Task DecideImport_NoPromptTrue_RunsWithoutPrompting() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: true, authSatisfied: true, skipImport: false, noPrompt: true,
            promptYesNo: () => throw new InvalidOperationException("must not prompt"));

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Run);
    }

    [Test]
    public async Task DecideImport_Interactive_UserAccepts_Runs() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: true, authSatisfied: true, skipImport: false, noPrompt: false,
            promptYesNo: () => true);

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Run);
    }

    [Test]
    public async Task DecideImport_Interactive_UserDeclines_SkipsWithNoReason() {
        var decision = SetupDecisions.DecideImport(
            hasCurrentRepo: true, authSatisfied: true, skipImport: false, noPrompt: false,
            promptYesNo: () => false);

        await Assert.That(decision.Outcome).IsEqualTo(SetupDecisions.ImportOutcome.Skip);
        await Assert.That(decision.SkipReason).IsNull();
    }

    // --- Applying the browser's Agents answer (WithBrowserAnswer) ---

    static CodingAgentsStep.Options Flags(
            bool skipCursor = false, bool skipCursorMcp = false,
            bool skipKiro = false, bool skipKiroMcp = false) => new(
        SkipClaude: false, SkipCodex: false, SkipCursor: skipCursor, SkipCopilot: false,
        NoPrompt: false, SkipKiro: skipKiro, SkipCursorMcp: skipCursorMcp, SkipKiroMcp: skipKiroMcp);

    static FirstRunAgentsAnswer Answer(params FirstRunAgentsChoice[] choices) =>
        new(choices, new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero), Unrecognised: 0);

    [Test]
    public async Task WithBrowserAnswer_NoAnswer_LeavesTheFlagsAlone() {
        var options = Flags(skipCursor: true);

        await Assert.That(SetupDecisions.WithBrowserAnswer(options, null)).IsEqualTo(options);
    }

    [Test]
    public async Task WithBrowserAnswer_InstallsWhatTheBrowserTurnedOn() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(), Answer(new FirstRunAgentsChoice(HarnessId.Cursor, Record: true, Tools: true)));

        await Assert.That(options.SkipCursor).IsFalse();
        await Assert.That(options.SkipCursorMcp).IsFalse();
    }

    // A harness left off is ABSENT from the answer rather than present-and-false, so "not named" has
    // to read as "leave it alone" — the opposite reading installs everything the user declined.
    [Test]
    public async Task WithBrowserAnswer_LeavesAHarnessTheAnswerNeverNamed() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(), Answer(new FirstRunAgentsChoice(HarnessId.Cursor, Record: true, Tools: true)));

        await Assert.That(options.SkipCodex).IsTrue();
        await Assert.That(options.SkipGemini).IsTrue();
    }

    // A flag is an instruction for THIS run — a script's opt-out. A browser answer does not override
    // one, or `--skip-cursor-hooks` would silently stop meaning anything on a flow-enabled tenant.
    [Test]
    public async Task WithBrowserAnswer_TheFlagStillWinsOverABrowserYes() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(skipCursor: true, skipCursorMcp: true),
            Answer(new FirstRunAgentsChoice(HarnessId.Cursor, Record: true, Tools: true)));

        await Assert.That(options.SkipCursor).IsTrue();
        await Assert.That(options.SkipCursorMcp).IsTrue();
    }

    // --skip-<vendor>-hooks alone is a WHOLE-VENDOR opt-out. Its MCP flag is separate and usually
    // absent, so without carrying the hooks flag across, a browser answer that ticks the vendor's
    // tools would reinterpret the exclusion as "capture off, tools on" and write its MCP config —
    // a write the caller explicitly opted out of, on a path that never asks again.
    [Test]
    public async Task WithBrowserAnswer_TheHooksFlagAloneStillBlocksThatVendorsTools() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(skipCursor: true),
            Answer(new FirstRunAgentsChoice(HarnessId.Cursor, Record: true, Tools: true)));

        await Assert.That(options.SkipCursor).IsTrue();
        await Assert.That(options.SkipCursorMcp).IsTrue();
    }

    // Kiro is the one vendor whose hooks flag is not a whole-vendor opt-out: the terminal path
    // registers its MCP under --skip-kiro-hooks and prints a line saying so. The browser path has
    // to agree, or one flag means two things depending on whether a browser answered.
    [Test]
    public async Task WithBrowserAnswer_TheKiroHooksFlagLeavesItsToolsAlone() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(skipKiro: true),
            Answer(new FirstRunAgentsChoice(HarnessId.Kiro, Record: true, Tools: true)));

        await Assert.That(options.SkipKiro).IsTrue();
        await Assert.That(options.SkipKiroMcp).IsFalse();
    }

    [Test]
    public async Task WithBrowserAnswer_TheKiroMcpFlagStillBlocksItsTools() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(skipKiroMcp: true),
            Answer(new FirstRunAgentsChoice(HarnessId.Kiro, Record: true, Tools: true)));

        await Assert.That(options.SkipKiroMcp).IsTrue();
    }

    [Test]
    public async Task WithBrowserAnswer_HonoursRecordWithoutToolsForAVendorThatSeparatesThem() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(), Answer(new FirstRunAgentsChoice(HarnessId.Cursor, Record: true, Tools: false)));

        await Assert.That(options.SkipCursor).IsFalse();
        await Assert.That(options.SkipCursorMcp).IsTrue();
    }

    [Test]
    public async Task WithBrowserAnswer_ADeclineSkipsEveryVendor() {
        var options = SetupDecisions.WithBrowserAnswer(Flags(), Answer());

        await Assert.That(options.SkipClaude).IsTrue();
        await Assert.That(options.SkipCodex).IsTrue();
        await Assert.That(options.SkipCursor).IsTrue();
        await Assert.That(options.SkipCopilot).IsTrue();
        await Assert.That(options.SkipGemini).IsTrue();
        await Assert.That(options.SkipKiro).IsTrue();
        await Assert.That(options.SkipPi).IsTrue();
        await Assert.That(options.SkipOpenCode).IsTrue();
        await Assert.That(options.SkipAntigravity).IsTrue();
    }

    // The browser asked everything this step would ask, so the step must not stop for a prompt — a
    // terminal waiting on input nobody is watching is how an unattended-looking flow hangs.
    [Test]
    public async Task WithBrowserAnswer_SilencesTheStepsPrompts() {
        var options = SetupDecisions.WithBrowserAnswer(
            Flags(), Answer(new FirstRunAgentsChoice(HarnessId.Codex, Record: true, Tools: true)));

        await Assert.That(options.NoPrompt).IsTrue();
    }
}
