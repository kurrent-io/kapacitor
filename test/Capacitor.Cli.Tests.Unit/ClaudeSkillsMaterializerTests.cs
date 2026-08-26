using Capacitor.Cli.Core.Skills;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeSkillsMaterializerTests {
    static SkillSnapshotItem Item(string slug) => new() {
        DocId = Guid.NewGuid(), Slug = slug, Title = "T", Description = "When.", Body = "Body.",
        Version = 1, ContentHash = "h1",
    };

    [Test]
    public async Task Drift_detection_covers_missing_edited_and_untracked_files() {
        using var tmp = new TempDir();
        var root = tmp.Path;
        var item = Item("retry-rules");
        ClaudeSkillsMaterializer.Write(root, item);
        var dir      = ClaudeSkillsMaterializer.SkillDirFor(root, item.Slug);
        var rendered = SkillsSyncPlanner.RenderSkillFile(item);
        var entry = new SkillsManifestEntry {
            DocId = item.DocId, Slug = item.Slug, Version = 1, ContentHash = "h1",
            Path = dir, FileHash = ClaudeSkillsMaterializer.FileHash(rendered),
        };

        await Assert.That(ClaudeSkillsMaterializer.HasDrifted(entry)).IsFalse();          // served as written
        File.AppendAllText(Path.Combine(dir, "SKILL.md"), "tampered");
        await Assert.That(ClaudeSkillsMaterializer.HasDrifted(entry)).IsTrue();           // edited
        Directory.Delete(dir, recursive: true);
        await Assert.That(ClaudeSkillsMaterializer.HasDrifted(entry)).IsTrue();           // deleted
        await Assert.That(ClaudeSkillsMaterializer.HasDrifted(entry with { FileHash = null })).IsTrue();   // pre-hash manifest
    }

    [Test]
    public async Task Prune_deletes_only_direct_kcap_children_of_the_root() {
        using var tmp = new TempDir();
        var root = tmp.Path;
        var owned = Path.Combine(root, "kcap-mine");
        var nested = Path.Combine(root, "user-owned", "kcap-nested");
        var sibling = root + "-backup";
        var siblingChild = Path.Combine(sibling, "kcap-foo");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(siblingChild);
        try {
            static SkillsManifestEntry E(string p) => new() {
                DocId = Guid.NewGuid(), Slug = "s", Version = 1, ContentHash = "h", Path = p,
            };
            ClaudeSkillsMaterializer.Prune(root, E(owned));
            ClaudeSkillsMaterializer.Prune(root, E(nested));
            ClaudeSkillsMaterializer.Prune(root, E(siblingChild));

            await Assert.That(Directory.Exists(owned)).IsFalse();
            await Assert.That(Directory.Exists(nested)).IsTrue();        // nested user dir untouched
            await Assert.That(Directory.Exists(siblingChild)).IsTrue();  // sibling root untouched
        } finally {
            Directory.Delete(sibling, recursive: true);
        }
    }
}
