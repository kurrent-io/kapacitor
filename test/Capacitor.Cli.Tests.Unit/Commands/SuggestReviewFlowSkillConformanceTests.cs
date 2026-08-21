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
    public async Task Description_stays_within_the_1024_char_skill_limit() {
        // Strict harnesses (e.g. Copilot) reject a skill whose description exceeds 1024 chars and
        // then SILENTLY fail to load it — so the skill never triggers there. Keep the folded
        // description under the cap. (An over-length description shipped only in a held branch once;
        // this guard stops it recurring.)
        await Assert.That(FoldedDescription().Length).IsLessThanOrEqualTo(1024);
    }

    // The folded description — whitespace collapsed, as YAML unfolds a `>-` block and as the harness
    // actually sees it. Raw-text substring checks are fragile to line wrapping (a wrapped phrase looks
    // absent), so pin against the folded form.
    static string FoldedDescription() {
        var m = System.Text.RegularExpressions.Regex.Match(SkillText(), @"(?m)^description: >-\n((?:^  .*\n)+)");
        return System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    [Test]
    public async Task Description_carries_both_milestone_triggers() {
        var d = FoldedDescription();
        await Assert.That(d).Contains("implementation is complete");
        await Assert.That(d).Contains("spec is finalized");
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
