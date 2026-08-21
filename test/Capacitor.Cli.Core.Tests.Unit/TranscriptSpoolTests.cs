namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptSpoolTests {
    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task Append_within_cap_returns_Appended_and_keeps_content() {
        using var tmp = new TempDir();
        var spool = new TranscriptSpool(tmp.Path, capBytes: 4096);
        var r = spool.Append(Sid, """{"lines":["a"],"line_numbers":[0]}""");
        await Assert.That(r).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();
        await Assert.That(spool.NeedsImport(Sid)).IsFalse();
    }

    [Test]
    public async Task Append_over_cap_marks_needs_import_and_never_drops_oldest() {
        using var tmp = new TempDir();
        var spool = new TranscriptSpool(tmp.Path, capBytes: 64); // tiny cap
        spool.Append(Sid, """{"lines":["first"],"line_numbers":[0]}""");   // fits
        var second = spool.Append(Sid, new string('x', 200));               // exceeds cap
        await Assert.That(second).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
        await Assert.That(spool.NeedsImport(Sid)).IsTrue();
        // The already-spooled first batch is preserved (no drop-oldest).
        var body = string.Concat(Directory.EnumerateFiles(tmp.Path, $"{Sid}.transcript.jsonl").Select(File.ReadAllText));
        await Assert.That(body).Contains("first");
    }

    [Test]
    public async Task Append_marks_needs_import_when_the_live_write_fails() {
        using var tmp = new TempDir();
        // Make the live spool path a DIRECTORY so File.AppendAllText throws, while the spool
        // The directory itself stays writable so the sibling needs-import marker can still persist.
        tmp.CreateDir($"{Sid}.transcript.jsonl");

        var spool = new TranscriptSpool(tmp.Path);
        var r = spool.Append(Sid, """{"lines":["a"],"line_numbers":[0]}""");
        // No silent drop: a failed write is surfaced as needs-import, never a phantom Appended.
        await Assert.That(r).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
        await Assert.That(spool.NeedsImport(Sid)).IsTrue();
    }

    /// <summary>
    /// The key must stay filename-safe: it becomes the basename, and the drain parses the prefix
    /// before the first dot. Anything carrying a separator is still rejected.
    /// </summary>
    [Test]
    [Arguments("has.a.dot")]
    [Arguments("has/slash")]
    [Arguments("has\\backslash")]
    [Arguments("")]
    public async Task Append_ignores_a_session_id_that_would_break_path_parsing(string sessionId) {
        using var tmp = new TempDir();
        var spool = new TranscriptSpool(tmp.Path);
        var r = spool.Append(sessionId, """{"n":1}""");
        await Assert.That(r).IsEqualTo(TranscriptSpool.AppendResult.Ignored);
    }

    /// <summary>
    /// Vendor ids that are not dashless GUIDs are accepted. The old 32-hex-only rule silently
    /// dropped every OpenCode payload, so its transcript spool never held anything.
    /// </summary>
    [Test]
    [Arguments("ses_7f3a9c21b8")]
    [Arguments("not-a-valid-sid")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Append_accepts_any_filename_safe_session_id(string sessionId) {
        using var tmp = new TempDir();
        var spool = new TranscriptSpool(tmp.Path);
        var r = spool.Append(sessionId, """{"n":1}""");
        await Assert.That(r).IsNotEqualTo(TranscriptSpool.AppendResult.Ignored);
        await Assert.That(spool.HasBacklog(sessionId)).IsTrue();
    }

    [Test]
    public async Task Drain_delivers_in_fifo_and_clears_file() {
        using var tmp = new TempDir();
        var spool = new TranscriptSpool(tmp.Path);
        spool.Append(Sid, """{"n":1}""");
        spool.Append(Sid, """{"n":2}""");
        var seen = new List<string>();
        await spool.DrainAsync(Sid, body => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                               () => false, CancellationToken.None);
        await Assert.That(seen).IsEquivalentTo(["""{"n":1}""", """{"n":2}"""]);
        await Assert.That(spool.HasBacklog(Sid)).IsFalse();
    }
}
