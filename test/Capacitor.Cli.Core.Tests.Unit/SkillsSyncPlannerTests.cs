using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit;

public class SkillsSyncPlannerTests {
    static SkillSnapshotItem Item(Guid doc, string slug = "s-1", int version = 1, string hash = "h1") => new() {
        DocId = doc, Slug = slug, Title = "T", Description = "When to use.", Body = "Body.",
        Version = version, ContentHash = hash,
    };

    static SkillsManifestEntry Entry(SkillSnapshotItem i, string path = "/skills/kcap-s-1") => new() {
        DocId = i.DocId, Slug = i.Slug, Version = i.Version, ContentHash = i.ContentHash, Path = path,
    };

    [Test]
    public async Task A_new_doc_is_written_and_an_absent_one_pruned() {
        var (keep, gone, fresh) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var manifest = new SkillsManifest { Skills = [
            Entry(Item(keep, "keep")), Entry(Item(gone, "gone"), "/skills/kcap-gone"),
        ] };
        var plan = SkillsSyncPlanner.Plan(manifest, [Item(keep, "keep"), Item(fresh, "fresh")]);

        await Assert.That(plan.Writes.Select(w => w.DocId)).IsEquivalentTo([fresh]);
        await Assert.That(plan.Prunes.Select(p => p.DocId)).IsEquivalentTo([gone]);
        await Assert.That(plan.Unchanged.Select(u => u.DocId)).IsEquivalentTo([keep]);
    }

    [Test]
    public async Task A_reapproved_version_rewrites_in_place() {
        var doc = Guid.NewGuid();
        var manifest = new SkillsManifest { Skills = [Entry(Item(doc))] };
        var plan = SkillsSyncPlanner.Plan(manifest, [Item(doc, version: 2, hash: "h2")]);

        await Assert.That(plan.Writes.Select(w => w.DocId)).IsEquivalentTo([doc]);
        await Assert.That(plan.Prunes).IsEmpty();   // same slug ⇒ same directory, overwritten
    }

    [Test]
    public async Task A_retitle_prunes_the_old_slug_and_writes_the_new() {
        // doc_id is the reconciliation identity (the server contract): a retitled doc keeps its id
        // but changes slug, so the old directory must go and the new one appear — never both.
        var doc = Guid.NewGuid();
        var manifest = new SkillsManifest { Skills = [Entry(Item(doc, "old-name"), "/skills/kcap-old-name")] };
        var plan = SkillsSyncPlanner.Plan(manifest, [Item(doc, "new-name")]);

        await Assert.That(plan.Writes.Select(w => w.Slug)).IsEquivalentTo(["new-name"]);
        await Assert.That(plan.Prunes.Select(p => p.Path)).IsEquivalentTo(["/skills/kcap-old-name"]);
    }

    [Test]
    public async Task An_empty_snapshot_prunes_everything_the_manifest_owns() {
        // Central revocation: the snapshot is the complete canonical set, so absence IS removal.
        var manifest = new SkillsManifest { Skills = [
            Entry(Item(Guid.NewGuid(), "a"), "/skills/kcap-a"),
            Entry(Item(Guid.NewGuid(), "b"), "/skills/kcap-b"),
        ] };
        var plan = SkillsSyncPlanner.Plan(manifest, []);
        await Assert.That(plan.Writes).IsEmpty();
        await Assert.That(plan.Prunes.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("retry-rules-ab12cd34", true)]
    [Arguments("a", true)]
    [Arguments("", false)]
    [Arguments("has/slash", false)]
    [Arguments("has\\backslash", false)]
    [Arguments("..", false)]
    [Arguments("Upper-Case", false)]
    [Arguments("dot.name", false)]
    public async Task Slug_safety_admits_only_single_lowercase_segments(string slug, bool safe) {
        await Assert.That(SkillsSyncPlanner.IsSafeSlug(slug)).IsEqualTo(safe);
    }

    [Test]
    public async Task Renders_frontmatter_with_a_quoted_description() {
        var text = SkillsSyncPlanner.RenderSkillFile(Item(Guid.NewGuid(), "retry-rules") with {
            Description = "Use when \"retrying\" appends:\nafter a conflict.",
        });
        await Assert.That(text.StartsWith("---\nname: retry-rules\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text).Contains("description: \"Use when \\\"retrying\\\" appends:\\nafter a conflict.\"");
        await Assert.That(text.EndsWith("---\n\nBody.\n", StringComparison.Ordinal)).IsTrue();
    }
}
