using System.Text.Json;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests the pure decision logic of <see cref="CodexSessionRolloutLocator"/> — the daemon's
/// best-effort fallback that links a hosted Codex reviewer to its session id when the
/// session-start hook doesn't land. Codex writes each session to
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-&lt;ts&gt;-&lt;uuid&gt;.jsonl</c>; the session id is the
/// filename UUID (== the hook-reported <c>session_id</c> for a top-level CLI session), and the
/// cwd lives in the opening <c>session_meta</c> envelope's <c>payload.cwd</c> (NOT at the JSONL
/// root the way Claude stores it).
/// </summary>
public class CodexSessionRolloutLocatorTests {
    const string Cwd = "/Users/dev/repo";

    // cwd is JSON-encoded via JsonSerializer (not naive quote-wrapping): a real Windows path
    // contains single backslashes, which are invalid unescaped inside a JSON string and were
    // silently corrupting every TryLocate fixture below on windows-latest CI (JsonNode.Parse
    // threw, MatchRollout fell back to CwdMatch.Unknown, and TryLocate always returned null).
    static string Meta(string cwd) =>
        "{\"type\":\"session_meta\",\"payload\":{\"id\":\"019f0021-29e3-7461-a781-e2646e16e271\","
      + "\"session_id\":\"019f0021-29e3-7461-a781-e2646e16e271\",\"cwd\":" + JsonSerializer.Serialize(cwd)
      + ",\"originator\":\"codex-tui\"}}";

    // ── cwd match / mismatch (payload.cwd, not root cwd) ─────────────────

    [Test]
    public async Task CwdMatch_ReturnsYes() {
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta(Cwd)], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task ForeignCwd_ReturnsNo() {
        // The user's own concurrent session in a different repo — must not be claimed.
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta("/Users/dev/other")], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.No);
    }

    [Test]
    public async Task RootLevelCwd_IsIgnored_ReturnsUnknown() {
        // Claude puts cwd at the root; Codex does NOT. A root-level cwd must not be treated
        // as a Codex match, or a Claude transcript would be mis-parsed.
        var claudeShaped = $$"""{"type":"user","cwd":"{{Cwd}}"}""";

        await Assert.That(CodexSessionRolloutLocator.MatchRollout([claudeShaped], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Unknown);
    }

    [Test]
    public async Task NoCwdYet_ReturnsUnknown() {
        // A freshly-created rollout whose session_meta line isn't flushed yet.
        var eventOnly = """{"type":"event_msg","payload":{"type":"task_started"}}""";

        await Assert.That(CodexSessionRolloutLocator.MatchRollout([eventOnly], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Unknown);
    }

    [Test]
    public async Task TrailingSeparator_IsTolerated() {
        await Assert.That(CodexSessionRolloutLocator.MatchRollout([Meta(Cwd + "/")], Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task InvalidJsonLines_AreSkipped() {
        string[] lines = [
            "not json",
            """{"type":"session_meta","payload":{"cwd":123}}""", // cwd not a string
            """{"truncated":"partial""",
            Meta(Cwd)
        ];

        await Assert.That(CodexSessionRolloutLocator.MatchRollout(lines, Cwd, StringComparison.Ordinal))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    [Test]
    public async Task WindowsCaseInsensitive_DifferentCase_Matches() {
        // A single-backslash path — Meta() now JSON-encodes it correctly itself; no more
        // manual pre-doubling to work around the old naive string concatenation.
        var lines = new[] { Meta(@"C:\Users\Dev\Repo") };

        await Assert.That(CodexSessionRolloutLocator.MatchRollout(lines, @"c:\users\dev\repo", StringComparison.OrdinalIgnoreCase))
            .IsEqualTo(CodexSessionRolloutLocator.CwdMatch.Yes);
    }

    // ── TryLocate over a real ~/.codex/sessions-shaped tree ──────────────

    // Encodes the creation instant into the rollout FILENAME — Codex's own scheme
    // (rollout-<yyyy-MM-ddTHH-mm-ss>-<uuid>.jsonl, local time under a local-dated day folder),
    // which is exactly what the locator reads. NOT File.SetCreationTimeUtc: that is a no-op on
    // Linux, so a creation time set that way collapses to "now" on the CI runner and the fixtures
    // below could not express "older than spawn".
    static string WriteRollout(string sessionsRoot, string uuid, string cwd, DateTime creationUtc) {
        var local = creationUtc.ToLocalTime();
        var dir   = Path.Combine(sessionsRoot, local.ToString("yyyy"), local.ToString("MM"), local.ToString("dd"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"rollout-{local:yyyy-MM-dd}T{local:HH-mm-ss}-{uuid}.jsonl");
        File.WriteAllText(file, Meta(cwd) + "\n");
        return file;
    }

    [Test]
    public async Task TryLocate_returns_dashless_session_id_of_the_matching_rollout() {
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            var uuid  = "019f0022-1702-7a02-a630-edbfb043add4";
            var wt    = Path.Combine(root, "worktree");
            WriteRollout(root, uuid, wt, creationUtc: spawn);

            var id = CodexSessionRolloutLocator.TryLocate(root, wt, spawn.AddSeconds(-1));

            await Assert.That(id).IsEqualTo(uuid.Replace("-", ""));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_ignores_a_foreign_cwd_rollout() {
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            WriteRollout(root, "019f0022-1702-7a02-a630-edbfb043add4", "/some/other/cwd", creationUtc: spawn);

            var id = CodexSessionRolloutLocator.TryLocate(root, Path.Combine(root, "worktree"), spawn.AddSeconds(-1));

            await Assert.That(id).IsNull();
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_missing_sessions_root_returns_null() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-codexrollout-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.That(CodexSessionRolloutLocator.TryLocate(missing, "/wt", DateTime.UtcNow)).IsNull();
    }

    // ── IsNewEnough: creation-time-only eligibility ──────────────────────

    [Test]
    public async Task IsNewEnough_creation_at_spawn_is_true() {
        var spawn = DateTime.UtcNow;
        await Assert.That(CodexSessionRolloutLocator.IsNewEnough(spawn, spawn)).IsTrue();
    }

    [Test]
    public async Task IsNewEnough_creation_well_before_spawn_is_false_even_if_recently_written() {
        // The bug: a much-older rollout must fail eligibility on creation time alone — an
        // ongoing write to it (last-write time) is no longer part of the equation at all.
        var spawn    = DateTime.UtcNow;
        var creation = spawn.AddMinutes(-10);

        await Assert.That(CodexSessionRolloutLocator.IsNewEnough(creation, spawn)).IsFalse();
    }

    [Test]
    public async Task IsNewEnough_creation_just_within_skew_tolerance_is_true() {
        var spawn = DateTime.UtcNow;
        await Assert.That(CodexSessionRolloutLocator.IsNewEnough(spawn.AddSeconds(-4), spawn)).IsTrue();
    }

    // ── TryLocate: creation-time eligibility + closest-after-spawn selection ─────
    // Creation time is expressed via the rollout FILENAME stamp (see WriteRollout) — the locator
    // reads that, not File.GetCreationTimeUtc, precisely because file birth time is un-settable
    // and unreliable on Linux. Last-write time is deliberately not a factor: the locator ignores
    // it, so an "older session still being appended to" is modeled purely by an old filename stamp.

    [Test]
    public async Task TryLocate_ignores_an_older_same_cwd_rollout_still_being_written() {
        // An older, unrelated rollout in the same (borrowed) cwd that a live process is still
        // appending to must NOT be mistaken for the freshly-spawned reviewer. Its recent writes
        // are irrelevant (the locator never reads last-write); only its old creation stamp counts.
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            var wt    = Path.Combine(root, "worktree");

            const string olderUuid = "019f0022-0000-7a02-a630-edbfb043add4";
            WriteRollout(root, olderUuid, wt, creationUtc: spawn.AddMinutes(-10));

            const string newerUuid = "019f0022-1111-7a02-a630-edbfb043add5";
            WriteRollout(root, newerUuid, wt, creationUtc: spawn.AddSeconds(2));

            var id = CodexSessionRolloutLocator.TryLocate(root, wt, spawn);

            await Assert.That(id).IsEqualTo(newerUuid.Replace("-", ""));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_returns_null_when_only_a_pre_spawn_rollout_matches() {
        // Same shape as above minus the reviewer's own rollout: nothing eligible remains, so
        // TryLocate must not fall back to the stale pre-spawn match.
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            var wt    = Path.Combine(root, "worktree");

            WriteRollout(root, "019f0022-0000-7a02-a630-edbfb043add4", wt, creationUtc: spawn.AddMinutes(-10));

            var id = CodexSessionRolloutLocator.TryLocate(root, wt, spawn);

            await Assert.That(id).IsNull();
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryLocate_prefers_the_rollout_created_closest_after_spawn_over_an_earlier_eligible_one() {
        // Both rollouts pass the creation-based eligibility window (one via the clock-skew
        // slack, created just before spawn), so eligibility alone can't disambiguate them.
        // Picking the numerically EARLIEST creation would wrongly prefer the pre-spawn one —
        // the reviewer's own rollout is always created at/after its own spawn, so the
        // at/after candidate must win.
        var root = Directory.CreateTempSubdirectory("kcap-codexrollout-").FullName;
        try {
            var spawn = DateTime.UtcNow;
            var wt    = Path.Combine(root, "worktree");

            const string beforeUuid = "019f0022-2222-7a02-a630-edbfb043add6";
            WriteRollout(root, beforeUuid, wt, creationUtc: spawn.AddSeconds(-3));

            const string afterUuid = "019f0022-3333-7a02-a630-edbfb043add7";
            WriteRollout(root, afterUuid, wt, creationUtc: spawn.AddSeconds(2));

            var id = CodexSessionRolloutLocator.TryLocate(root, wt, spawn);

            await Assert.That(id).IsEqualTo(afterUuid.Replace("-", ""));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
