using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit;

public class RepoEvidenceScannerTests {
    sealed record FakeRepo(string? Owner, string? RepoName);

    // Roots the fake findRoot knows; everything else resolves to null (non-repo).
    static string? FakeFindRoot(string dir) =>
        dir.StartsWith("/home/u/dev/repo-a", StringComparison.Ordinal) ? "/home/u/dev/repo-a"
        : dir.StartsWith("/home/u/dev/repo-b", StringComparison.Ordinal) ? "/home/u/dev/repo-b"
        : dir.StartsWith("/home/u/dev/repo-noremote", StringComparison.Ordinal) ? "/home/u/dev/repo-noremote"
        : null;

    // Roots the fake resolver can turn into a repo. repo-noremote resolves (FindRoot succeeds,
    // it IS a git dir) but comes back owner/repo null — e.g. a local `git init` with no remote.
    static readonly Dictionary<string, FakeRepo> Repos = new(StringComparer.Ordinal) {
        ["/home/u/dev/repo-a"]        = new("acme", "repo-a"),
        ["/home/u/dev/repo-b"]        = new("acme", "repo-b"),
        ["/home/u/dev/repo-noremote"] = new(null, null),
    };

    static Task<FakeRepo?> FakeResolve(string root) => Task.FromResult(Repos.GetValueOrDefault(root));

    static bool IsComplete(FakeRepo r) => r.Owner is not null && r.RepoName is not null;

    static RepoEvidenceScanner<FakeRepo> NewScanner() => new(FakeFindRoot, FakeResolve, IsComplete);

    static string Line(string tool, string key, string path) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{tool}}}","input":{"{{{key}}}":"{{{path}}}"}}]}}""";

    [Test]
    public async Task First_mutation_path_attributes_immediately() {
        var s          = NewScanner();
        var attributed = await s.OnLineAsync("claude", Line("Edit", "file_path", "/home/u/dev/repo-a/src/x.cs"));
        await Assert.That(attributed).IsEqualTo(new FakeRepo("acme", "repo-a"));
        await Assert.That(s.Done).IsTrue();
    }

    [Test]
    public async Task Read_path_is_remembered_not_attributed() {
        var s          = NewScanner();
        var attributed = await s.OnLineAsync("claude", Line("Read", "file_path", "/home/u/dev/repo-b/y.cs"));
        await Assert.That(attributed).IsNull();
        await Assert.That(s.Done).IsFalse();
        await Assert.That(s.ReadFallback).IsEqualTo(new FakeRepo("acme", "repo-b"));
    }

    [Test]
    public async Task Mutation_after_read_wins_slot_a() {
        var s = NewScanner();
        await s.OnLineAsync("claude", Line("Read", "file_path", "/home/u/dev/repo-b/y.cs"));
        var attributed = await s.OnLineAsync("claude", Line("Write", "file_path", "/home/u/dev/repo-a/z.cs"));
        await Assert.That(attributed).IsEqualTo(new FakeRepo("acme", "repo-a"));
    }

    [Test]
    public async Task Promote_read_fallback_only_when_no_mutation_arrived() {
        var s = NewScanner();
        await s.OnLineAsync("claude", Line("Grep", "path", "/home/u/dev/repo-b/sub"));
        var promoted = await s.PromoteReadFallbackAsync();
        await Assert.That(promoted).IsEqualTo(new FakeRepo("acme", "repo-b"));
        await Assert.That(s.Done).IsTrue();
        await Assert.That(await s.PromoteReadFallbackAsync()).IsNull(); // idempotent
    }

    [Test]
    public async Task Scanner_stops_after_attribution() {
        var s = NewScanner();
        await s.OnLineAsync("claude", Line("Edit", "file_path", "/home/u/dev/repo-a/x.cs"));
        var again = await s.OnLineAsync("claude", Line("Edit", "file_path", "/home/u/dev/repo-b/q.cs"));
        await Assert.That(again).IsNull(); // no post-attribution hunting (spec D2)
    }

    [Test]
    public async Task Relative_temp_and_nonrepo_paths_are_ignored() {
        var s = NewScanner();
        await Assert.That(await s.OnLineAsync("claude", Line("Edit", "file_path", "relative/x.cs"))).IsNull();
        await Assert.That(await s.OnLineAsync("claude", Line("Edit", "file_path", "/tmp/scratch/x.cs"))).IsNull();
        await Assert.That(s.Done).IsFalse();
        await Assert.That(s.ReadFallback).IsNull();
    }

    [Test]
    public async Task Mutation_with_failed_tool_result_still_counts() {
        // Evidence is the INPUT (spec D5); tool results are never consulted, so
        // there is nothing on the line that could veto it.
        var s          = NewScanner();
        var attributed = await s.OnLineAsync("claude", Line("NotebookEdit", "notebook_path", "/home/u/dev/repo-a/n.ipynb"));
        await Assert.That(attributed).IsEqualTo(new FakeRepo("acme", "repo-a"));
    }

    [Test]
    public async Task Unknown_vendor_and_malformed_lines_are_fail_open() {
        var s = NewScanner();
        await Assert.That(await s.OnLineAsync("gemini", Line("Edit", "file_path", "/home/u/dev/repo-a/x.cs"))).IsNull();
        await Assert.That(await s.OnLineAsync("claude", "not json at all")).IsNull();
        await Assert.That(await s.OnLineAsync("claude", """{"type":"user"}""")).IsNull();
    }

    [Test]
    public async Task Incomplete_root_does_not_latch_and_a_later_complete_root_wins() {
        // FindRoot succeeds for repo-noremote (it IS a git dir) but the resolver comes back
        // owner/repo null — must be skipped, not latched, so a later real repo can still win.
        var s = NewScanner();

        var firstAttempt = await s.OnLineAsync("claude", Line("Edit", "file_path", "/home/u/dev/repo-noremote/x.cs"));
        await Assert.That(firstAttempt).IsNull();
        await Assert.That(s.Done).IsFalse();

        var secondAttempt = await s.OnLineAsync("claude", Line("Edit", "file_path", "/home/u/dev/repo-a/y.cs"));
        await Assert.That(secondAttempt).IsEqualTo(new FakeRepo("acme", "repo-a"));
        await Assert.That(s.Done).IsTrue();
    }

    [Test]
    public async Task ExtractClaudePaths_classifies_all_v1_tools() {
        var muts = new[] { ("Edit", "file_path"), ("MultiEdit", "file_path"), ("Write", "file_path"), ("NotebookEdit", "notebook_path") };
        foreach (var (tool, key) in muts) {
            var got = RepoEvidencePaths.ExtractClaudePaths(Line(tool, key, "/a/b.cs"));
            await Assert.That(got).HasSingleItem();
            await Assert.That(got[0].Kind).IsEqualTo(RepoEvidenceKind.Mutation);
        }
        var reads = new[] { ("Read", "file_path"), ("Glob", "path"), ("Grep", "path") };
        foreach (var (tool, key) in reads) {
            var got = RepoEvidencePaths.ExtractClaudePaths(Line(tool, key, "/a/b"));
            await Assert.That(got).HasSingleItem();
            await Assert.That(got[0].Kind).IsEqualTo(RepoEvidenceKind.Read);
        }
    }

    // qodo #3 / Windows CI: a Windows-absolute path must attribute exactly like a Unix one — the
    // gate and the parent-dir derivation both have to be OS-independent (this suite runs on
    // ubuntu-latest AND windows-latest), not just accept whatever the running OS calls "absolute".
    // Line() splices `path` straight into JSON text (no JSON-escaping), so a Windows path's
    // backslashes must be pre-doubled in the literal here — JsonNode.Parse then unescapes `\\`
    // back to the real single-backslash path the scanner actually receives (verified: a
    // single-backslash literal produces invalid JSON, e.g. `\d` is not a legal JSON escape).
    const string WindowsPathJsonEscaped = @"C:\\dev\\repo-a\\x.cs";
    const string WindowsPath            = @"C:\dev\repo-a\x.cs";

    [Test]
    public async Task Windows_absolute_path_attributes_correctly() {
        static string? WindowsFindRoot(string dir) => dir.StartsWith(@"C:\dev\repo-a", StringComparison.Ordinal) ? @"C:\dev\repo-a" : null;

        var s = new RepoEvidenceScanner<FakeRepo>(
            WindowsFindRoot, _ => Task.FromResult<FakeRepo?>(new("acme", "repo-a")), IsComplete);

        var attributed = await s.OnLineAsync("claude", Line("Edit", "file_path", WindowsPathJsonEscaped));
        await Assert.That(attributed).IsEqualTo(new FakeRepo("acme", "repo-a"));
        await Assert.That(s.Done).IsTrue();
    }

    [Test]
    public async Task ExtractClaudePaths_accepts_a_windows_drive_path() {
        var got = RepoEvidencePaths.ExtractClaudePaths(Line("Edit", "file_path", WindowsPathJsonEscaped));
        await Assert.That(got).HasSingleItem();
        await Assert.That(got[0].Path).IsEqualTo(WindowsPath);
    }

    // qodo #2: a tool_use block can sit at the event's TOP-LEVEL content, not only nested under
    // message.content — both shapes occur in real Claude transcripts.
    [Test]
    public async Task ExtractClaudePaths_reads_top_level_content_too() {
        const string line = """{"type":"assistant","content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/a/b.cs"}}]}""";
        var          got  = RepoEvidencePaths.ExtractClaudePaths(line);
        await Assert.That(got).HasSingleItem();
        await Assert.That(got[0].Path).IsEqualTo("/a/b.cs");
        await Assert.That(got[0].Kind).IsEqualTo(RepoEvidenceKind.Mutation);
    }
}
