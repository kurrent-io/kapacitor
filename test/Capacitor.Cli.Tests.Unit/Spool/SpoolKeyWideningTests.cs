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
    // A REAL OpenCode id: its generator appends a base62 suffix, so ids are MIXED CASE. An earlier
    // version of this test invented a lowercase-only value, which masked a fix that would have
    // rejected almost every genuine OpenCode session.
    const string OpenCodeId = "ses_619a78374ffe7o0x1iTK74jFRg";

    readonly string _dir  = Path.Combine(Path.GetTempPath(), $"kcap-widen-{Guid.NewGuid():N}");
    readonly string _tdir = Path.Combine(Path.GetTempPath(), $"kcap-widen-t-{Guid.NewGuid():N}");

    public void Dispose() {
        foreach (var d in new[] { _dir, _tdir }) { try { Directory.Delete(d, true); } catch { } }
    }

    [Test]
    public async Task Lifecycle_filename_is_a_reversible_escape_not_a_digest() {
        var spool = new HookSpool(_dir);

        await Assert.That(spool.Append(OpenCodeId, "session-start/opencode", """{"session_id":"x"}""")).IsTrue();

        var files = Directory.GetFiles(_dir, "*.jsonl");
        await Assert.That(files.Length).IsEqualTo(1);

        var basename = Path.GetFileNameWithoutExtension(files[0]);

        // Single-cased for filesystem safety, but every original character is still recoverable —
        // a digest would lose the id, and the drain posts it back as session_id.
        await Assert.That(basename).IsEqualTo(basename.ToLowerInvariant());
        await Assert.That(basename.Replace("~", "")).IsEqualTo(OpenCodeId.ToLowerInvariant());
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
    [Arguments("ses_619a78374ffe7o0x1iTK74jFRg")]      // real, mixed-case
    [Arguments("ses_ABCDEF")]                           // all-uppercase suffix
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]     // the pre-existing dashless-GUID form
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]     // and its uppercase spelling
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

    /// <summary>
    /// Two ids differing ONLY by case are distinct sessions and must not share a file. The filename
    /// is escaped into a single case for exactly this reason — on macOS/Windows the raw ids would
    /// collide, interleaving one session's payloads into the other's spool.
    /// </summary>
    [Test]
    public async Task Ids_differing_only_by_case_do_not_share_a_spool_file() {
        var spool = new HookSpool(_dir);

        await Assert.That(spool.Append("ses_aBcD", "session-start/opencode", """{"which":"lower"}""")).IsTrue();
        await Assert.That(spool.Append("ses_AbCd", "session-start/opencode", """{"which":"upper"}""")).IsTrue();

        // Two distinct files on disk — on a case-insensitive filesystem the raw ids would be one.
        var files = Directory.GetFiles(_dir, "*.jsonl");
        await Assert.That(files.Length).IsEqualTo(2);

        // And neither file holds the other session's payload.
        foreach (var f in files) {
            var text = File.ReadAllText(f);
            await Assert.That(text.Contains("lower") && text.Contains("upper")).IsFalse();
        }

        await Assert.That(spool.HasBacklog("ses_aBcD")).IsTrue();
        await Assert.That(spool.HasBacklog("ses_AbCd")).IsTrue();
    }

    /// <summary>
    /// The escape is reversible: the drain posts the DECODED id, so a lossy transform would put a
    /// fabricated session_id on the wire.
    /// </summary>
    [Test]
    [Arguments("ses_619a78374ffe7o0x1iTK74jFRg")]
    [Arguments("ses_ABCDEF")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task The_drained_session_id_is_the_original_byte_for_byte(string sessionId) {
        var spool = new HookSpool(_dir);
        spool.Append(sessionId, "session-start/opencode", $$"""{"session_id":"{{sessionId}}"}""");

        string? posted = null;

        await spool.DrainAllAsync(
            currentSessionId: null,
            poster: (_, body) => { posted = body; return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None);

        await Assert.That(posted).IsNotNull();
        await Assert.That(posted!).Contains(sessionId);
    }

    /// <summary>
    /// Upgrade safety. These files are written RAW, exactly as a pre-upgrade kcap left them —
    /// creating them through <c>Append</c> would run the new encoder and never reach this shape.
    /// A dashless GUID must therefore keep its historical filename: two hex spellings differing only
    /// by case are the same id, so they need no escaping and must not be renamed out from under a
    /// running install.
    /// </summary>
    [Test]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments("aAbBcCdDeEfF00112233445566778899")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task A_legacy_raw_spool_file_is_still_found_and_drained(string legacyId) {
        Directory.CreateDirectory(_dir);
        var legacyPath = Path.Combine(_dir, $"{legacyId}.jsonl");
        File.WriteAllText(legacyPath,
            $$"""{"route":"session-start/claude","body":"{\"session_id\":\"{{legacyId}}\"}"}""" + "\n");

        var spool = new HookSpool(_dir);

        // Found by the id-keyed lookup...
        await Assert.That(spool.HasBacklog(legacyId)).IsTrue();

        // ...and by the directory sweep, which must hand back the id the file was named with.
        string? posted = null;
        await spool.DrainAllAsync(
            currentSessionId: null,
            poster: (_, body) => { posted = body; return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5),
            ct: CancellationToken.None);

        await Assert.That(posted).IsNotNull();
        await Assert.That(posted!).Contains(legacyId);
        await Assert.That(File.Exists(legacyPath)).IsFalse(); // consumed, not stranded
    }

    [Test]
    public async Task A_legacy_raw_ended_marker_is_still_honoured() {
        // If the marker were invisible the drain would forget a terminal event had been delivered and
        // let a straggler through after session end.
        const string legacyId = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, $".ended-{legacyId}"), "");

        await Assert.That(new HookSpool(_dir).IsMarkedEnded(legacyId)).IsTrue();
    }

    [Test]
    public async Task A_legacy_raw_transcript_file_is_still_found() {
        const string legacyId = "aAbBcCdDeEfF00112233445566778899";
        Directory.CreateDirectory(_tdir);
        File.WriteAllText(Path.Combine(_tdir, $"{legacyId}.transcript.jsonl"), "{\"n\":1}\n");

        await Assert.That(new TranscriptSpool(_tdir).HasBacklog(legacyId)).IsTrue();
    }
}
