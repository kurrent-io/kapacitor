using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The skill is model-driven: its frontmatter description is the whole trigger surface, and
/// its body carries the load-bearing guardrails. These pins keep the two milestones and the safety
/// invariants in the shipped file — a reword that drops one would otherwise pass CI silently (same
/// stance as FlowsDriverSchemaConformanceTests).</summary>
public class SuggestReviewFlowSkillConformanceTests {
    static string SkillText() => File.ReadAllText(
        Path.Combine(RepoTree.Root(), "kcap", "skills", "suggest-review-flow", "SKILL.md"));

    [Test]
    public async Task Registered_in_source_names()
        => await Assert.That(AgentsSkillsInstaller.SourceNames).Contains("suggest-review-flow");

    [Test]
    public async Task Description_carries_both_milestone_triggers() {
        var text = SkillText();
        await Assert.That(text).Contains("implementation is complete");
        await Assert.That(text).Contains("spec is finalized");
    }

    [Test]
    public async Task Body_pins_the_never_auto_run_guard()
        => await Assert.That(SkillText()).Contains("MUST NOT start a review flow until the user affirmatively accepts");

    [Test]
    public async Task Body_defers_execution_to_review_flows()
        => await Assert.That(SkillText()).Contains("review-flows");

    [Test]
    public async Task Body_calls_the_availability_tool()
        => await Assert.That(SkillText()).Contains("list_reviewer_vendors");

    [Test]
    public async Task Body_handles_ordinary_self_review_locally()
        => await Assert.That(SkillText()).Contains("perform that review locally");

    [Test]
    public async Task Body_unknown_driver_never_claims_a_different_model()
        => await Assert.That(SkillText()).Contains("do NOT claim \"a different model\"");
}
