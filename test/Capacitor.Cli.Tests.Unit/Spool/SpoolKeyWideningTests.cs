using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Spool;

/// <summary>
/// The spool key is WIDENED, not hashed. The filename IS the session id —
/// <c>LifecycleSpoolDrain</c> posts it verbatim as <c>session_id</c> for
/// <c>session-needs-import</c> — so hashing would put a fabricated id on the wire.
///
/// <para>The old dashless-GUID form silently dropped every OpenCode payload (<c>ses_…</c>), for both
/// the lifecycle and transcript spools: <c>Append</c> returned void, so the caller reported "spooled"
/// while nothing was written.</para>
/// </summary>
public class SpoolKeyWideningTests : IDisposable {
    // A real OpenCode id shape: never 32 hex, with or without dash-stripping.
    const string OpenCodeId = "ses_7f3a9c21b8";

    readonly string _dir  = Path.Combine(Path.GetTempPath(), $"kcap-widen-{Guid.NewGuid():N}");
    readonly string _tdir = Path.Combine(Path.GetTempPath(), $"kcap-widen-t-{Guid.NewGuid():N}");

    public void Dispose() {
        foreach (var d in new[] { _dir, _tdir }) { try { Directory.Delete(d, true); } catch { } }
    }

    [Test]
    public async Task Lifecycle_basename_is_the_raw_id_not_a_digest() {
        var spool = new HookSpool(_dir);

        await Assert.That(spool.Append(OpenCodeId, "session-start/opencode", """{"session_id":"ses_7f3a9c21b8"}""")).IsTrue();
        // The exact basename, which is what discriminates widening from hashing — a hashing
        // implementation would also keep the raw id inside the payload.
        await Assert.That(File.Exists(Path.Combine(_dir, $"{OpenCodeId}.jsonl"))).IsTrue();
        await Assert.That(spool.HasBacklog(OpenCodeId)).IsTrue();
    }

    [Test]
    public async Task Transcript_basename_is_the_raw_id() {
        var transcript = new TranscriptSpool(_tdir);

        transcript.Append(OpenCodeId, """{"line":1}""");

        await Assert.That(transcript.HasBacklog(OpenCodeId)).IsTrue();
    }

    [Test]
    public async Task Append_reports_failure_instead_of_silently_dropping() {
        // A path that cannot be a directory: the write must be reported, not swallowed.
        var blocked = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "not a directory");

        await Assert.That(new HookSpool(blocked).Append(OpenCodeId, "session-start/opencode", "{}")).IsFalse();
    }

    [Test]
    [Arguments("ses_7f3a9c21b8")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // the pre-existing dashless-GUID form still works
    [Arguments("a-b_c-123")]
    public async Task Widened_keys_survive_a_drain_round_trip(string sessionId) {
        var spool = new HookSpool(_dir);
        spool.Append(sessionId, "session-start/opencode", $$"""{"session_id":"{{sessionId}}"}""");

        var delivered = new List<(string Route, string Body)>();

        await spool.DrainAllAsync(
            currentSessionId: sessionId,
            poster: (route, body) => { delivered.Add((route, body)); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None);

        await Assert.That(delivered.Count).IsEqualTo(1);
        await Assert.That(delivered[0].Route).IsEqualTo("session-start/opencode");
        // The id on the wire is the original, never a derived key.
        await Assert.That(delivered[0].Body).Contains(sessionId);
        await Assert.That(spool.HasBacklog(sessionId)).IsFalse();
    }

    [Test]
    [Arguments("has.a.dot")]     // '.' is the field separator in {sid}.{pid}-{seq}.draining
    [Arguments("has/slash")]     // path traversal
    [Arguments("has\\backslash")]
    [Arguments("")]
    public async Task Keys_that_would_break_path_parsing_are_still_rejected(string sessionId) {
        await Assert.That(new HookSpool(_dir).Append(sessionId, "session-start/opencode", "{}")).IsFalse();
    }
}
