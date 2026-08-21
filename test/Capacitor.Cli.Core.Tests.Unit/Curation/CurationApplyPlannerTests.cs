using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Core.Tests.Unit.Curation;

public class CurationApplyPlannerTests {
    const string Root = "/repo";
    static string Claude => System.IO.Path.Combine(Root, "CLAUDE.md");
    static string Agents => System.IO.Path.Combine(Root, "AGENTS.md");
    static CuratedGuideline G(string t) => new("quality", t);

    [Test]
    public async Task Creates_agents_when_neither_exists() {
        var plan = CurationApplyPlanner.BuildPlan(
            Root,
            new Dictionary<string, string?> { [Claude] = null, [Agents] = null },
            [G("alpha")]);

        await Assert.That(plan.Files.Count).IsEqualTo(1);
        var f = plan.Files[0];
        await Assert.That(f.Path).IsEqualTo(Agents);
        await Assert.That(f.Action).IsEqualTo(CurateAction.Create);
        await Assert.That(f.Added).IsEquivalentTo(new[] { "alpha" });
        await Assert.That(f.NewContent!.Contains("- alpha")).IsTrue();
    }

    [Test]
    public async Task Update_diffs_added_and_removed() {
        var existing = CuratedBlock.Splice("# T\n", CuratedBlock.Render([G("keep"), G("drop")])!);
        var plan = CurationApplyPlanner.BuildPlan(
            Root,
            new Dictionary<string, string?> { [Claude] = existing, [Agents] = null },
            [G("keep"), G("new")]);

        var f = plan.Files.Single(p => p.Path == Claude);
        await Assert.That(f.Action).IsEqualTo(CurateAction.Update);
        await Assert.That(f.Added).IsEquivalentTo(new[] { "new" });
        await Assert.That(f.Removed).IsEquivalentTo(new[] { "drop" });
    }

    [Test]
    public async Task Noop_when_block_already_current() {
        var existing = CuratedBlock.Splice("# T\n", CuratedBlock.Render([G("same")])!);
        var plan = CurationApplyPlanner.BuildPlan(
            Root,
            new Dictionary<string, string?> { [Claude] = existing },
            [G("same")]);
        await Assert.That(plan.Files.Single().Action).IsEqualTo(CurateAction.NoOp);
    }

    [Test]
    public async Task Empty_guidelines_removes_existing_block() {
        var existing = CuratedBlock.Splice("# T\nkeep\n", CuratedBlock.Render([G("x")])!);
        var plan = CurationApplyPlanner.BuildPlan(
            Root,
            new Dictionary<string, string?> { [Claude] = existing, [Agents] = null },
            []);

        var f = plan.Files.Single(p => p.Path == Claude);
        await Assert.That(f.Action).IsEqualTo(CurateAction.Remove);
        await Assert.That(f.Removed).IsEquivalentTo(new[] { "x" });
        await Assert.That(f.NewContent!.Contains(CuratedBlock.StartMarker)).IsFalse();
    }
}
