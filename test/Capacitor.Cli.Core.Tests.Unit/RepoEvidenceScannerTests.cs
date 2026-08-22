using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit;

public class RepoEvidenceScannerTests {
    // Roots the fake resolver knows; everything else resolves to null (non-repo).
    static string? FakeFindRoot(string dir) =>
        dir.StartsWith("/home/u/dev/repo-a", StringComparison.Ordinal) ? "/home/u/dev/repo-a"
        : dir.StartsWith("/home/u/dev/repo-b", StringComparison.Ordinal) ? "/home/u/dev/repo-b"
        : null;

    static string Line(string tool, string key, string path) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{tool}}}","input":{"{{{key}}}":"{{{path}}}"}}]}}""";

    [Test]
    public async Task First_mutation_path_attributes_immediately() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        var attributed = s.OnLine("claude", Line("Edit", "file_path", "/home/u/dev/repo-a/src/x.cs"));
        await Assert.That(attributed).IsEqualTo("/home/u/dev/repo-a");
        await Assert.That(s.Done).IsTrue();
    }

    [Test]
    public async Task Read_path_is_remembered_not_attributed() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        var attributed = s.OnLine("claude", Line("Read", "file_path", "/home/u/dev/repo-b/y.cs"));
        await Assert.That(attributed).IsNull();
        await Assert.That(s.Done).IsFalse();
        await Assert.That(s.ReadFallbackRoot).IsEqualTo("/home/u/dev/repo-b");
    }

    [Test]
    public async Task Mutation_after_read_wins_slot_a() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        s.OnLine("claude", Line("Read", "file_path", "/home/u/dev/repo-b/y.cs"));
        var attributed = s.OnLine("claude", Line("Write", "file_path", "/home/u/dev/repo-a/z.cs"));
        await Assert.That(attributed).IsEqualTo("/home/u/dev/repo-a");
    }

    [Test]
    public async Task Promote_read_fallback_only_when_no_mutation_arrived() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        s.OnLine("claude", Line("Grep", "path", "/home/u/dev/repo-b/sub"));
        var promoted = s.PromoteReadFallback();
        await Assert.That(promoted).IsEqualTo("/home/u/dev/repo-b");
        await Assert.That(s.Done).IsTrue();
        await Assert.That(s.PromoteReadFallback()).IsNull(); // idempotent
    }

    [Test]
    public async Task Scanner_stops_after_attribution() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        s.OnLine("claude", Line("Edit", "file_path", "/home/u/dev/repo-a/x.cs"));
        var again = s.OnLine("claude", Line("Edit", "file_path", "/home/u/dev/repo-b/q.cs"));
        await Assert.That(again).IsNull(); // no post-attribution hunting (spec D2)
    }

    [Test]
    public async Task Relative_temp_and_nonrepo_paths_are_ignored() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        await Assert.That(s.OnLine("claude", Line("Edit", "file_path", "relative/x.cs"))).IsNull();
        await Assert.That(s.OnLine("claude", Line("Edit", "file_path", "/tmp/scratch/x.cs"))).IsNull();
        await Assert.That(s.Done).IsFalse();
        await Assert.That(s.ReadFallbackRoot).IsNull();
    }

    [Test]
    public async Task Mutation_with_failed_tool_result_still_counts() {
        // Evidence is the INPUT (spec D5); tool results are never consulted, so
        // there is nothing on the line that could veto it.
        var s = new RepoEvidenceScanner(FakeFindRoot);
        var attributed = s.OnLine("claude", Line("NotebookEdit", "notebook_path", "/home/u/dev/repo-a/n.ipynb"));
        await Assert.That(attributed).IsEqualTo("/home/u/dev/repo-a");
    }

    [Test]
    public async Task Unknown_vendor_and_malformed_lines_are_fail_open() {
        var s = new RepoEvidenceScanner(FakeFindRoot);
        await Assert.That(s.OnLine("gemini", Line("Edit", "file_path", "/home/u/dev/repo-a/x.cs"))).IsNull();
        await Assert.That(s.OnLine("claude", "not json at all")).IsNull();
        await Assert.That(s.OnLine("claude", """{"type":"user"}""")).IsNull();
    }

    [Test]
    public async Task ExtractClaudePaths_classifies_all_v1_tools() {
        var muts = new[] { ("Edit","file_path"), ("MultiEdit","file_path"), ("Write","file_path"), ("NotebookEdit","notebook_path") };
        foreach (var (tool, key) in muts) {
            var got = RepoEvidenceScanner.ExtractClaudePaths(Line(tool, key, "/a/b.cs"));
            await Assert.That(got).HasSingleItem();
            await Assert.That(got[0].Kind).IsEqualTo(RepoEvidenceKind.Mutation);
        }
        var reads = new[] { ("Read","file_path"), ("Glob","path"), ("Grep","path") };
        foreach (var (tool, key) in reads) {
            var got = RepoEvidenceScanner.ExtractClaudePaths(Line(tool, key, "/a/b"));
            await Assert.That(got).HasSingleItem();
            await Assert.That(got[0].Kind).IsEqualTo(RepoEvidenceKind.Read);
        }
    }
}
