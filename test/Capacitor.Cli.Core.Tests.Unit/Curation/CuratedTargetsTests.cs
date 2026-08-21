using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Core.Tests.Unit.Curation;

public class CuratedTargetsTests {
    const string Root = "/repo";
    static string Claude => System.IO.Path.Combine(Root, "CLAUDE.md");
    static string Agents => System.IO.Path.Combine(Root, "AGENTS.md");

    [Test]
    public async Task With_content_both_exist_targets_both() {
        var r = CuratedTargets.Resolve(Root, claudeMdExists: true, agentsMdExists: true, hasContent: true);
        await Assert.That(r).IsEquivalentTo(new[] { Claude, Agents });
    }

    [Test]
    public async Task With_content_only_one_targets_that_one() {
        await Assert.That(CuratedTargets.Resolve(Root, true, false, true)).IsEquivalentTo(new[] { Claude });
        await Assert.That(CuratedTargets.Resolve(Root, false, true, true)).IsEquivalentTo(new[] { Agents });
    }

    [Test]
    public async Task With_content_neither_creates_agents() {
        await Assert.That(CuratedTargets.Resolve(Root, false, false, true)).IsEquivalentTo(new[] { Agents });
    }

    [Test]
    public async Task Empty_set_never_creates_targets_only_existing() {
        await Assert.That(CuratedTargets.Resolve(Root, false, false, false)).IsEmpty();
        await Assert.That(CuratedTargets.Resolve(Root, true, false, false)).IsEquivalentTo(new[] { Claude });
        await Assert.That(CuratedTargets.Resolve(Root, true, true, false)).IsEquivalentTo(new[] { Claude, Agents });
    }
}
