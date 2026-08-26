using System.Text;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// the runtime two-zone rewrite guard. Each test gets its own config root, so asserting
/// CursorMarkers.IsQuarantined after a detected rewrite reads only this test's own markers.
/// </summary>
public class CursorRewriteGuardTests {
    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task VerifyPriorZone_true_when_no_checkpoint_recorded_yet() {
        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());

        await Assert.That(guard.VerifyPriorZone("anything")).IsTrue();
    }

    [Test]
    public async Task VerifyPriorZone_true_when_hash_matches_the_checkpoint() {
        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());
        guard.Checkpoint(offset: 100, trailingSha: "abc123");

        await Assert.That(guard.VerifyPriorZone("abc123")).IsTrue();
    }

    [Test]
    public async Task VerifyPriorZone_false_and_quarantines_on_a_mismatch() {
        var sid   = NewSessionId();
        var guard = new CursorRewriteGuard(Config.Root, sid);
        guard.Checkpoint(offset: 100, trailingSha: "abc123");

        var ok = guard.VerifyPriorZone("different-hash");

        await Assert.That(ok).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
    }

    [Test]
    public async Task VerifyNewRange_true_when_the_re_read_bytes_are_unchanged() {
        var guard    = new CursorRewriteGuard(Config.Root, NewSessionId());
        var original = Encoding.UTF8.GetBytes("line1\nline2\n");

        guard.RecordNewRangeRead(oldOffset: 0, sampledLength: original.Length, readBytes: original);

        await Assert.That(guard.VerifyNewRange(original, oldOffset: 0, sampledLength: original.Length)).IsTrue();
    }

    // The critical case: the runtime guard must catch a rewrite of the batch it JUST read, not
    // only a rewrite of bytes prior to the checkpoint. Same length as the original read (so this
    // isn't the shrink case below), different content — simulates the file being rewritten in
    // place between the initial read and the immediately-before-send re-read.
    [Test]
    public async Task VerifyNewRange_false_and_quarantines_when_the_newly_read_bytes_are_mutated_between_read_and_send() {
        var sid      = NewSessionId();
        var guard    = new CursorRewriteGuard(Config.Root, sid);
        var original = Encoding.UTF8.GetBytes("line1\nline2\n");
        guard.RecordNewRangeRead(oldOffset: 0, sampledLength: original.Length, readBytes: original);

        var mutated = Encoding.UTF8.GetBytes("lineX\nline2\n"); // same length, first line rewritten

        var ok = guard.VerifyNewRange(mutated, oldOffset: 0, sampledLength: original.Length);

        await Assert.That(ok).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
    }

    [Test]
    public async Task VerifyNewRange_false_and_quarantines_on_a_length_shrink() {
        var sid      = NewSessionId();
        var guard    = new CursorRewriteGuard(Config.Root, sid);
        var original = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        guard.RecordNewRangeRead(oldOffset: 0, sampledLength: original.Length, readBytes: original);

        var shrunk = Encoding.UTF8.GetBytes("line1\n"); // fewer bytes re-read than were originally sampled

        var ok = guard.VerifyNewRange(shrunk, oldOffset: 0, sampledLength: original.Length);

        await Assert.That(ok).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
    }

    [Test]
    public async Task VerifyFullPrefix_seeds_on_first_call_and_true_on_a_pure_append() {
        var guard  = new CursorRewriteGuard(Config.Root, NewSessionId());
        var first  = Encoding.UTF8.GetBytes("line1\nline2\n");
        var second = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");

        var firstSample  = new CursorAppendOnlyProbe.Sample(first.Length, CursorAppendOnlyProbe.Sha256Hex(first));
        var secondSample = new CursorAppendOnlyProbe.Sample(second.Length, CursorAppendOnlyProbe.Sha256Hex(second));

        await Assert.That(guard.VerifyFullPrefix(firstSample, first)).IsTrue();   // seeds — nothing to compare yet
        await Assert.That(guard.VerifyFullPrefix(secondSample, second)).IsTrue(); // pure append
    }

    [Test]
    public async Task VerifyFullPrefix_false_and_quarantines_when_the_prefix_was_rewritten() {
        var sid    = NewSessionId();
        var guard  = new CursorRewriteGuard(Config.Root, sid);
        var first  = Encoding.UTF8.GetBytes("lineA\nlineB\n");
        var second = Encoding.UTF8.GetBytes("lineX\nlineB\nlineC\n"); // first line changed

        var firstSample  = new CursorAppendOnlyProbe.Sample(first.Length, CursorAppendOnlyProbe.Sha256Hex(first));
        var secondSample = new CursorAppendOnlyProbe.Sample(second.Length, CursorAppendOnlyProbe.Sha256Hex(second));

        await Assert.That(guard.VerifyFullPrefix(firstSample, first)).IsTrue();

        var ok = guard.VerifyFullPrefix(secondSample, second);

        await Assert.That(ok).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
    }

    // a shrink is unambiguous evidence of a rewrite on its own; the
    // watcher's original wiring gated the whole zone check behind "did the file grow", so a
    // shrink slipped through entirely undetected.
    [Test]
    public async Task VerifyNotShrunk_true_when_length_at_or_above_the_checkpoint() {
        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());

        await Assert.That(guard.VerifyNotShrunk(newLength: 100, checkpointOffset: 100)).IsTrue();
        await Assert.That(guard.VerifyNotShrunk(newLength: 150, checkpointOffset: 100)).IsTrue();
    }

    [Test]
    public async Task VerifyNotShrunk_false_and_quarantines_on_a_shrink() {
        var sid   = NewSessionId();
        var guard = new CursorRewriteGuard(Config.Root, sid);

        var ok = guard.VerifyNotShrunk(newLength: 4, checkpointOffset: 100);

        await Assert.That(ok).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
    }

    [Test]
    public async Task HashPriorZone_does_not_disturb_the_stream_position() {
        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());
        guard.Checkpoint(offset: 6, trailingSha: "irrelevant");

        using var tempDir = new TempDir();
        var       tmp     = tempDir.CreateFile("stream.tmp", "line1\nline2\n");

        await using var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = 3;

        guard.HashPriorZone(stream);

        await Assert.That(stream.Position).IsEqualTo(3L);
    }
}
