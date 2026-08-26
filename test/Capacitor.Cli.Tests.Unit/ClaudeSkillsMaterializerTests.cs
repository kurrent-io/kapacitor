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

public class SkillsAutoSyncTests {
    [Test]
    public async Task Throttle_skips_within_the_interval_and_runs_past_it() {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        static SkillsManifest M(DateTimeOffset t) => new() { SyncedAt = t };

        await Assert.That(Cli.Commands.SkillsCommand.AutoThrottled(M(now.AddHours(-1)), now)).IsTrue();
        await Assert.That(Cli.Commands.SkillsCommand.AutoThrottled(M(now.AddHours(-7)), now)).IsFalse();
        await Assert.That(Cli.Commands.SkillsCommand.AutoThrottled(null, now)).IsFalse();               // no ledger ⇒ sync
        await Assert.That(Cli.Commands.SkillsCommand.AutoThrottled(new() { SyncedAt = null }, now)).IsFalse();
    }

    [Test]
    public async Task Spawn_is_detached_quiet_and_never_throws() {
        System.Diagnostics.ProcessStartInfo? seen = null;
        Cli.Commands.SkillsAutoSync.ProcessStarterForTesting = psi => { seen = psi; return null; };
        try {
            Cli.Commands.SkillsAutoSync.SpawnDetached("/some/repo");
            await Assert.That(seen).IsNotNull();
            await Assert.That(seen!.ArgumentList.ToArray()).IsEquivalentTo(new[] { "skills", "sync", "--auto" });
            await Assert.That(seen.WorkingDirectory).IsEqualTo("/some/repo");
            await Assert.That(seen.RedirectStandardOutput).IsTrue();   // the hook's stdout is a data channel

            Cli.Commands.SkillsAutoSync.ProcessStarterForTesting = _ => throw new InvalidOperationException("boom");
            Cli.Commands.SkillsAutoSync.SpawnDetached("/some/repo");       // swallowed — never breaks a hook
        } finally {
            Cli.Commands.SkillsAutoSync.ProcessStarterForTesting = null;
        }
    }
}
