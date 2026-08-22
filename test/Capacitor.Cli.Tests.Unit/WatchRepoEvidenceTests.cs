using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Tests.Unit;

public class WatchRepoEvidenceTests {
    [Test]
    public async Task Refresh_never_clears_an_evidence_payload() {
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: true)).IsFalse();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: false)).IsTrue();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: new(), repositoryFromEvidence: true)).IsTrue();
    }

    static string? FakeFindRoot(string dir) => dir.StartsWith("/h/dev/repo-r", StringComparison.Ordinal) ? "/h/dev/repo-r" : null;

    static Task<RepositoryPayload?> FakeResolve(string root) =>
        Task.FromResult<RepositoryPayload?>(new() { Owner = "acme", RepoName = "repo-r" });

    static WatchState NewOutsideRepoState() => new() {
        EvidenceScanner = new RepoEvidenceScanner<RepositoryPayload>(
            FakeFindRoot, FakeResolve, p => p.Owner is not null && p.RepoName is not null)
    };

    static readonly string[] ReadOnlyLine = [
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/h/dev/repo-r/y.cs"}}]}}""",
    ];

    // Guards the Finding 1 regression directly: the read-only fallback must reach
    // state.Repository specifically on the FINAL drain, since that's the only batch that still
    // goes out on a clean session end (PostSessionEndOnParentExitAsync never fires there).
    [Test]
    public async Task FinalDrain_delivers_the_read_fallback_to_state_repository() {
        var state = NewOutsideRepoState();

        await WatchCommand.ApplyEvidenceScanAsync(state, "claude", ReadOnlyLine, isFinalDrain: true);

        await Assert.That(state.Repository).IsNotNull();
        await Assert.That(state.Repository!.RepoName).IsEqualTo("repo-r");
        await Assert.That(state.RepositoryFromEvidence).IsTrue();
    }

    [Test]
    public async Task NonFinalDrain_does_not_promote_the_read_fallback() {
        var state = NewOutsideRepoState();

        await WatchCommand.ApplyEvidenceScanAsync(state, "claude", ReadOnlyLine, isFinalDrain: false);

        await Assert.That(state.Repository).IsNull();
    }

    // qodo #1: File.ReadLines opens FileShare.Read (Windows-mandatory), which can block the
    // agent's own flush of the transcript it's actively writing. ReadLinesShared is the
    // FileShare.ReadWrite replacement (mirrors WatchCommand.ReadAllTextShared's rationale) —
    // this just guards it still reads lines correctly; the sharing behavior itself is
    // Windows-specific and not meaningfully observable from a cross-platform unit test.
    [Test]
    public async Task ReadLinesShared_reads_lines_from_a_real_file() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("transcript.jsonl", ["line one", "line two"]);

        var lines = WatchCommand.ReadLinesShared(path).ToList();

        await Assert.That(lines).IsEquivalentTo(["line one", "line two"]);
    }
}
