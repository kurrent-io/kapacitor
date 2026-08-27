using System.Text.Json;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>
/// Tests the pure decision logic of <see cref="SessionTranscriptLocator"/> — the daemon's
/// fallback that links a hosted Claude agent to its session when the session-start hook
/// fails (e.g. expired kcap token → every /hooks POST 401s and the agent page shows
/// "Waiting for session to start..." forever).
///
/// The daemon symlinks the worktree's Claude project dir to the SOURCE repo's, which is
/// shared with the user's own sessions — so a candidate transcript must be verified by its
/// <c>cwd</c> matching the agent's per-agent worktree path, and only files written at/after
/// the spawn time are considered.
/// </summary>
public class SessionTranscriptLocatorTests {
    const string Worktree = "/home/user/.kcap/worktrees/agent-1";

    static string Line(string cwd) => $$"""{"type":"user","cwd":"{{cwd}}","sessionId":"abc"}""";

    // ── TryMatchTranscript: cwd match / mismatch ─────────────────────────

    [Test]
    public async Task CwdMatch_ReturnsTrue() {
        var lines = new[] { Line(Worktree) };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CwdMismatch_ReturnsFalse() {
        // The user's own session in the source repo — same project dir (symlinked),
        // different cwd. Must NOT be claimed as the agent's session.
        var lines = new[] { Line("/home/user/dev/source-repo") };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task CwdOnLaterLine_StillMatches() {
        var lines = new[] {
            """{"type":"summary","summary":"hello"}""",
            Line(Worktree)
        };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsTrue();
    }

    // ── Windows semantics: case-insensitivity + separator tolerance ─────

    [Test]
    public async Task WindowsCaseInsensitive_DifferentCase_Matches() {
        var lines = new[] { Line(@"C:\\Users\\Dev\\.kcap\\Worktrees\\Agent-1") };

        var result = SessionTranscriptLocator.TryMatchTranscript(
            lines,
            @"c:\users\dev\.kcap\worktrees\agent-1",
            StringComparison.OrdinalIgnoreCase
        );

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CaseSensitiveComparison_DifferentCase_DoesNotMatch() {
        var lines = new[] { Line("/HOME/user/.kcap/worktrees/agent-1") };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TrailingSeparator_IsTolerated() {
        var lines = new[] { Line(Worktree + "/") };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task MixedSeparatorStyles_AreTolerated() {
        // cwd recorded with backslashes, worktree passed with forward slashes (or vice
        // versa) — both normalize to the same path.
        var lines = new[] { Line(@"C:\\w\\agent-1") };

        var result = SessionTranscriptLocator.TryMatchTranscript(lines, "C:/w/agent-1", StringComparison.OrdinalIgnoreCase);

        await Assert.That(result).IsTrue();
    }

    // ── Robustness against a concurrently-written file ───────────────────

    [Test]
    public async Task InvalidJsonLines_AreSkipped() {
        var lines = new[] {
            "not json at all",
            """{"type":"user","cwd":123}""",          // cwd not a string
            """{"truncated":"partial-write""",         // torn tail — file written concurrently
            Line(Worktree)
        };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task BlankLines_AreSkippedAndNotCounted() {
        var lines = new List<string> { "", "   " };
        lines.Add(Line(Worktree));

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NoCwdAnywhere_ReturnsFalse() {
        var lines = new[] { """{"type":"summary"}""", """{"type":"queue"}""" };

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task MatchBeyondLineCap_IsNotFound() {
        // The cwd appears on early lines in real transcripts; the cap bounds how much of
        // a (possibly huge) foreign transcript is parsed per poll tick.
        var lines = Enumerable
            .Repeat("""{"type":"assistant"}""", SessionTranscriptLocator.MaxLinesToInspect)
            .Append(Line(Worktree));

        await Assert.That(SessionTranscriptLocator.TryMatchTranscript(lines, Worktree, StringComparison.Ordinal)).IsFalse();
    }

    // ── MatchTranscript tri-state: drives the poll-loop's negative cache ─
    //
    // A definitive foreign cwd (No) is cached and never re-read; a file with no cwd yet
    // (Unknown) must NOT be cached, so the agent's own transcript is re-checked once Claude
    // writes its cwd line.

    [Test]
    public async Task MatchTranscript_MatchingCwd_ReturnsYes() {
        var lines = new[] { Line(Worktree) };

        await Assert.That(SessionTranscriptLocator.MatchTranscript(lines, Worktree, StringComparison.Ordinal))
            .IsEqualTo(SessionTranscriptLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task MatchTranscript_ForeignCwd_ReturnsNo() {
        // Seen a cwd, it's someone else's — permanent non-match, safe to cache.
        var lines = new[] { Line("/home/user/dev/source-repo") };

        await Assert.That(SessionTranscriptLocator.MatchTranscript(lines, Worktree, StringComparison.Ordinal))
            .IsEqualTo(SessionTranscriptLocator.CwdMatch.No);
    }

    [Test]
    public async Task MatchTranscript_NoCwdYet_ReturnsUnknown() {
        // A freshly-created transcript whose cwd line hasn't been written yet. Must be
        // Unknown (not No) so the poll loop keeps re-checking it — it could be ours.
        var lines = new[] { """{"type":"summary"}""", """{"truncated":"partial""" };

        await Assert.That(SessionTranscriptLocator.MatchTranscript(lines, Worktree, StringComparison.Ordinal))
            .IsEqualTo(SessionTranscriptLocator.CwdMatch.Unknown);
    }

    [Test]
    public async Task MatchTranscript_EmptyFile_ReturnsUnknown() {
        await Assert.That(SessionTranscriptLocator.MatchTranscript([], Worktree, StringComparison.Ordinal))
            .IsEqualTo(SessionTranscriptLocator.CwdMatch.Unknown);
    }

    // ── SessionIdFromFileName: dashed → dashless normalization ──────────

    [Test]
    public async Task DashedGuidFileName_NormalizesToDashlessLowercase() {
        var id = SessionTranscriptLocator.SessionIdFromFileName("A1B2C3D4-E5F6-7890-ABCD-EF0123456789.jsonl");

        await Assert.That(id).IsEqualTo("a1b2c3d4e5f67890abcdef0123456789");
    }

    [Test]
    public async Task DashlessGuidFileName_IsAccepted() {
        var id = SessionTranscriptLocator.SessionIdFromFileName("a1b2c3d4e5f67890abcdef0123456789.jsonl");

        await Assert.That(id).IsEqualTo("a1b2c3d4e5f67890abcdef0123456789");
    }

    [Test]
    public async Task FullPath_UsesBasenameOnly() {
        var id = SessionTranscriptLocator.SessionIdFromFileName(
            "/home/user/.claude/projects/-home-user-repo/a1b2c3d4-e5f6-7890-abcd-ef0123456789.jsonl"
        );

        await Assert.That(id).IsEqualTo("a1b2c3d4e5f67890abcdef0123456789");
    }

    [Test]
    public async Task NonGuidFileName_ReturnsNull() {
        await Assert.That(SessionTranscriptLocator.SessionIdFromFileName("notes.jsonl")).IsNull();
    }

    [Test]
    public async Task NonHexStemOfGuidLength_ReturnsNull() {
        // 32 chars once dashes are removed, but not hex.
        await Assert.That(SessionTranscriptLocator.SessionIdFromFileName("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.jsonl")).IsNull();
    }

    [Test]
    public async Task WrongExtension_ReturnsNull() {
        await Assert.That(SessionTranscriptLocator.SessionIdFromFileName("a1b2c3d4-e5f6-7890-abcd-ef0123456789.json")).IsNull();
    }

    // ── IsNewEnough: spawn-time filter ───────────────────────────────────

    static readonly DateTime SpawnedAt = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FileOlderThanSpawn_IsExcluded() {
        // A pre-existing user session in the shared (symlinked) project dir.
        var old = SpawnedAt.AddMinutes(-30);

        await Assert.That(SessionTranscriptLocator.IsNewEnough(old, old, SpawnedAt)).IsFalse();
    }

    [Test]
    public async Task FileCreatedAfterSpawn_IsIncluded() {
        var created = SpawnedAt.AddSeconds(4);

        await Assert.That(SessionTranscriptLocator.IsNewEnough(created, created, SpawnedAt)).IsTrue();
    }

    [Test]
    public async Task OldCreationButRecentWrite_IsIncluded() {
        // Newest of the two timestamps counts: e.g. a resumed transcript file whose
        // creation predates the spawn but which is actively being appended to.
        var creation  = SpawnedAt.AddHours(-1);
        var lastWrite = SpawnedAt.AddSeconds(10);

        await Assert.That(SessionTranscriptLocator.IsNewEnough(creation, lastWrite, SpawnedAt)).IsTrue();
    }

    [Test]
    public async Task FileWithinSkewToleranceBeforeSpawn_IsIncluded() {
        // Filesystem timestamp granularity / small clock skew must not exclude the
        // agent's own transcript. The cwd check is the real disambiguator.
        var justBefore = SpawnedAt.AddSeconds(-2);

        await Assert.That(SessionTranscriptLocator.IsNewEnough(justBefore, justBefore, SpawnedAt)).IsTrue();
    }

    [Test]
    public async Task FileWellBeforeSkewTolerance_IsExcluded() {
        var tooOld = SpawnedAt.AddSeconds(-6);

        await Assert.That(SessionTranscriptLocator.IsNewEnough(tooOld, tooOld, SpawnedAt)).IsFalse();
    }

    // ── TryLocateWinner: the matched file, link-resolved ────────────────

    // Encoded, not interpolated: a Windows path's backslashes are JSON escapes.
    static string TranscriptLine(string cwd) => $$$"""{"type":"user","cwd":"{{{JsonEncodedText.Encode(cwd)}}}","sessionId":"abc","message":{"content":"hi"}}""";

    [Test]
    public async Task TryLocateWinner_returns_id_and_the_matched_path() {
        using var tmp = new TempDir();
        var projectDir = tmp.CreateDir("projects", "-repo").Path;
        var worktree = tmp.PathTo("wt");
        var file = tmp.CreateFile(["projects", "-repo", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(worktree) + "\n");

        var winner = SessionTranscriptLocator.TryLocateWinner(projectDir, worktree, DateTime.UtcNow.AddMinutes(-1));

        await Assert.That(winner?.SessionId).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(winner?.Path).IsEqualTo(file);
    }

    [Test]
    public async Task Winner_through_a_symlinked_project_dir_survives_the_symlink_going_away() {
        using var tmp = new TempDir();
        var real = tmp.CreateDir("projects", "-source").Path;
        var link = tmp.PathTo("projects", "-worktree");
        Directory.CreateSymbolicLink(link, real);
        var worktree = tmp.PathTo("wt");
        tmp.CreateFile(["projects", "-source", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(worktree) + "\n");

        var winner = SessionTranscriptLocator.TryLocateWinner(link, worktree, DateTime.UtcNow.AddMinutes(-1));

        await Assert.That(winner?.Path).IsEqualTo(Path.Combine(real, "0123456789abcdef0123456789abcdef.jsonl"));

        Directory.Delete(link);
        File.AppendAllText(winner!.Value.Path, TranscriptLine(worktree) + "\n");
        await Assert.That(File.ReadLines(winner.Value.Path).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task A_freshly_appended_transcript_with_a_foreign_cwd_is_never_a_winner() {
        // The boundary a Claude --resume into a new worktree hits: the resumed transcript's
        // early records keep their original cwd, and no amount of fresh writes changes that.
        using var tmp = new TempDir();
        var projectDir = tmp.CreateDir("projects", "-repo").Path;
        tmp.CreateFile(["projects", "-repo", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(tmp.PathTo("original-cwd")) + "\n");
        var ruledOut = new HashSet<string>();

        var winner = SessionTranscriptLocator.TryLocateWinner(projectDir, tmp.PathTo("new-worktree"), DateTime.UtcNow.AddMinutes(-1), ruledOut);

        await Assert.That(winner).IsNull();
        await Assert.That(ruledOut).Count().IsEqualTo(1);
    }
}
