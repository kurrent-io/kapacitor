using Capacitor.Cli.Commands;

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
}
