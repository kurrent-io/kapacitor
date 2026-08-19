using System.Text;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Qodo #291: the streaming drain reader (ReadNewCompleteLinesAsync) replaces
// File.ReadAllTextAsync in DrainNewLines. It must (1) open with FileShare.ReadWrite so a
// concurrently-writing agent is never blocked (#291 #1), (2) NOT materialize the whole file
// (#291 #2), and (3) stay byte-for-byte behaviour-equivalent to the string helper
// (SplitNewCompleteLines) — same partial-final-line hold-back, so a mid-write final line is
// never consumed and dropped. The length-cap makes that hold-back robust against a concurrent
// append happening after the length was sampled.
public class ReadNewCompleteLinesAsyncTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string WriteTemp(string content) => Tmp.CreateFile($"drain-{Guid.NewGuid():N}.jsonl", content);

    static async Task<WatchCommand.NewTranscriptLines> ReadViaStream(
            string path, int linesProcessed, WatchCommand.IncompleteFinalLinePolicy policy) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await WatchCommand.ReadNewCompleteLinesAsync(stream, linesProcessed, policy, default);
    }

    // the Cursor watcher's captureRawBytes: true path.
    static async Task<WatchCommand.NewTranscriptLines> ReadViaStreamRaw(
            string path, int linesProcessed, WatchCommand.IncompleteFinalLinePolicy policy) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await WatchCommand.ReadNewCompleteLinesAsync(stream, linesProcessed, policy, default, captureRawBytes: true);
    }

    async Task AssertParity(string content, int linesProcessed, WatchCommand.IncompleteFinalLinePolicy policy) {
        var expected = WatchCommand.SplitNewCompleteLines(content, linesProcessed, policy);

        var path = WriteTemp(content);
        var actual = await ReadViaStream(path, linesProcessed, policy);

        await Assert.That(actual.Lines).IsEquivalentTo(expected.Lines);
        await Assert.That(actual.LineNumbers).IsEquivalentTo(expected.LineNumbers);
        await Assert.That(actual.NextPosition).IsEqualTo(expected.NextPosition);
        await Assert.That(actual.HeldIncompleteFinalLine).IsEqualTo(expected.HeldIncompleteFinalLine);
    }

    // ---- 1. Parity with the string helper across the interesting shapes ----

    [Test]
    public async Task Parity_all_lines_when_file_ends_with_newline() =>
        await AssertParity("a\nb\nc\n", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_partial_final_line_held_back_when_no_trailing_newline() =>
        await AssertParity("a\nb\nc", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_blank_lines_skipped_but_counted() =>
        await AssertParity("a\n\nb\n", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_blank_partial_final_line() =>
        await AssertParity("real\n   ", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_respects_lines_already_processed() =>
        await AssertParity("a\nb\nc\n", 2, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_crlf_terminated_lines() =>
        await AssertParity("a\r\nb\r\n", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_lone_partial_line_holds_at_start() =>
        await AssertParity("partial-json-being-written", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    [Test]
    public async Task Parity_empty_file() =>
        await AssertParity("", 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

    // ---- 2. Final drain (ConsumeIfComplete): consume only a parseable unterminated final line ----

    [Test]
    public async Task Final_drain_consumes_a_parseable_unterminated_final_line() {
        var path = WriteTemp("{\"a\":1}\n{\"b\":2}");
        var r = await ReadViaStream(path, 0, WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(r.NextPosition).IsEqualTo(2);
        await Assert.That(r.HeldIncompleteFinalLine).IsFalse();
    }

    [Test]
    public async Task Final_drain_holds_an_unparseable_unterminated_final_line() {
        var path = WriteTemp("{\"a\":1}\n{\"b\":2");
        var r = await ReadViaStream(path, 0, WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0 });
        await Assert.That(r.NextPosition).IsEqualTo(1);
        await Assert.That(r.HeldIncompleteFinalLine).IsTrue();
    }

    // ---- 2b. TOCTOU guard: a final line that resumes GROWING after an earlier completeness
    // probe is re-validated on the bytes actually consumed and held, not sent-and-advanced. ----

    [Test]
    public async Task Final_drain_holds_a_line_that_grew_into_an_incomplete_record_after_the_completeness_check() {
        var path = WriteTemp("{\"a\":1}\n");
        // Step 1: the bounded completeness probe sees a fully complete file.
        await Assert.That(await WatchCommand.WaitForFinalLineCompletionAsync(path, attempts: 1, delayMs: 1)).IsTrue();

        // Step 2: the writer RESUMES, appending a new partial record (no newline, unparseable)
        // in the gap before the consuming read — the exact TOCTOU the fix must close.
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":2");

        // Step 3: the consuming final drain re-validates on the bytes it actually reads and
        // HOLDS the still-incomplete tail — only the complete first record is delivered.
        var r = await ReadViaStream(path, 0, WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
        await Assert.That(r.NextPosition).IsEqualTo(1);
        await Assert.That(r.HeldIncompleteFinalLine).IsTrue();
    }

    // ---- 3. FileShare.ReadWrite doesn't block a concurrent writer (#291 #1) ----

    [Test]
    public async Task Read_does_not_block_a_concurrent_append_writer() {
        var path = WriteTemp("a\nb\n");
        // Hold the file open for reading exactly the way DrainNewLines does.
        await using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // A concurrent agent appending to the same file must succeed (no sharing violation).
        // With default FileShare.Read this would throw IOException — the #291 #1 regression.
        await using (var writeStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        await using (var writer = new StreamWriter(writeStream)) {
            await writer.WriteLineAsync("c");
        }

        // The read still works while the handle is shared; the append landed before the read,
        // so all three complete lines are surfaced.
        var r = await WatchCommand.ReadNewCompleteLinesAsync(readStream, 0, WatchCommand.IncompleteFinalLinePolicy.Hold, default);
        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    // ---- 4. Cap correctness: reads stop at the sampled length ----

    [Test]
    public async Task Cap_stops_reading_at_the_sampled_length() {
        // The wrapper mirrors a length sampled BEFORE a concurrent append: bytes past `limit`
        // exist in `inner` but must never be surfaced, so an as-yet unterminated final line can't
        // be made to look complete and get consumed (the bug this PR fixes).
        var full  = Encoding.UTF8.GetBytes("a\nb\nc\n");   // 6 bytes
        var limit = 4L;                                     // "a\nb\n" only
        using var inner = new MemoryStream(full);

        var capped = new WatchCommand.LengthLimitedReadStream(inner, limit);

        var buffer = new byte[full.Length];
        var total  = 0;
        int n;
        while ((n = await capped.ReadAsync(buffer.AsMemory(total))) > 0) {
            total += n;
        }

        await Assert.That(total).IsEqualTo((int)limit);
        await Assert.That(Encoding.UTF8.GetString(buffer, 0, total)).IsEqualTo("a\nb\n");
    }

    [Test]
    public async Task Cap_appended_bytes_are_not_read_this_pass() {
        // End-to-end feel: sample the length while the file is "a\nb\n", append "c\n" AFTER
        // sampling, then read. The appended line must not appear this pass (it's re-read next pass).
        var path = WriteTemp("a\nb\n");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sampledLength = stream.Length;

        await using (var writeStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        await using (var writer = new StreamWriter(writeStream)) {
            await writer.WriteLineAsync("c");
        }

        // Read only up to the sampled length, exactly as ReadNewCompleteLinesAsync does internally.
        using var reader = new StreamReader(new WatchCommand.LengthLimitedReadStream(stream, sampledLength), leaveOpen: true);
        var text = await reader.ReadToEndAsync();

        await Assert.That(text).IsEqualTo("a\nb\n");
        await Assert.That(text).DoesNotContain("c");
    }

    [Test]
    public async Task Cap_sync_read_stops_at_the_sampled_length() {
        var full  = Encoding.UTF8.GetBytes("hello-world");
        var limit = 5L;
        using var inner = new MemoryStream(full);

        var capped = new WatchCommand.LengthLimitedReadStream(inner, limit);

        var buffer = new byte[full.Length];
        var total  = 0;
        int n;
        while ((n = capped.Read(buffer, total, buffer.Length - total)) > 0) {
            total += n;
        }

        await Assert.That(total).IsEqualTo((int)limit);
        await Assert.That(Encoding.UTF8.GetString(buffer, 0, total)).IsEqualTo("hello");
    }

    // 5. review fix #1: captureRawBytes threads the EXACT decode-read bytes back to
    // the caller. This is the mechanism the fix relies on to close the TOCTOU the round-2 review
    // found: the runtime rewrite guard used to decode lines from one read, then reopen the file
    // SEPARATELY to hash "the new range" — a rewrite landing between those two reads meant the
    // guard recorded/verified a snapshot the batch never actually came from. Binding the guard's
    // hash to SnapshotBytes (this same capped read) instead of a later reopen closes that window. ----

    [Test]
    public async Task CaptureRawBytes_snapshot_bytes_are_exactly_the_bytes_that_decoded_the_returned_lines() {
        var path = WriteTemp("line1\nline2\nline3\n");
        var r = await ReadViaStreamRaw(path, 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

        await Assert.That(r.SnapshotBytes).IsNotNull();
        await Assert.That(r.SnapshotBytes!.Length).IsEqualTo((int)r.SnapshotByteLength);

        // Independently decoding the SAME buffer must reproduce exactly the lines the
        // streaming read already decoded — proving SnapshotBytes really is "the bytes that
        // produced Lines", not bytes from some other (possibly later, possibly rewritten) read.
        var reDecoded = WatchCommand.SplitNewCompleteLines(
            Encoding.UTF8.GetString(r.SnapshotBytes!), 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

        await Assert.That(reDecoded.Lines).IsEquivalentTo(r.Lines);
        await Assert.That(reDecoded.LineNumbers).IsEquivalentTo(r.LineNumbers);
    }

    [Test]
    public async Task CaptureRawBytes_defaults_to_null_when_the_caller_does_not_opt_in() {
        // Every non-Cursor vendor keeps the original zero-buffering streaming path.
        var path = WriteTemp("line1\nline2\n");
        var r = await ReadViaStream(path, 0, WatchCommand.IncompleteFinalLinePolicy.Hold);
        await Assert.That(r.SnapshotBytes).IsNull();
    }

    [Test]
    public async Task CaptureRawBytes_stays_behaviour_equivalent_to_the_non_capturing_path() {
        const string content = "a\nb\n\nc";
        var pathA = WriteTemp(content);
        var pathB = WriteTemp(content);
        var expected = await ReadViaStream(pathA, 0, WatchCommand.IncompleteFinalLinePolicy.Hold);
        var actual   = await ReadViaStreamRaw(pathB, 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

        await Assert.That(actual.Lines).IsEquivalentTo(expected.Lines);
        await Assert.That(actual.LineNumbers).IsEquivalentTo(expected.LineNumbers);
        await Assert.That(actual.NextPosition).IsEqualTo(expected.NextPosition);
        await Assert.That(actual.HeldIncompleteFinalLine).IsEqualTo(expected.HeldIncompleteFinalLine);
    }

    // 6. review fix (r3, finding #3): rawBytesReadFrom/newRangeByteOffset bound the
    // captureRawBytes buffer instead of always materializing the whole file. A large, mostly-
    // unchanged Cursor transcript must NOT be re-read/re-allocated in full on every poll — only the
    // guard's own small trailing-tail zone plus whatever's actually new. The periodic full-prefix
    // cadence (a separate, rare poll) is the only time the whole file is still read; that path is
    // exercised by passing rawBytesReadFrom: 0 (the default), already covered above. ----

    static async Task<WatchCommand.NewTranscriptLines> ReadViaStreamRawBounded(
            string path, int linesProcessed, long rawBytesReadFrom, long newRangeByteOffset,
            WatchCommand.IncompleteFinalLinePolicy policy = WatchCommand.IncompleteFinalLinePolicy.Hold) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await WatchCommand.ReadNewCompleteLinesAsync(
            stream, linesProcessed, policy, default,
            captureRawBytes: true, rawBytesReadFrom: rawBytesReadFrom, newRangeByteOffset: newRangeByteOffset);
    }

    [Test]
    public async Task Bounded_capture_allocates_only_the_window_from_rawBytesReadFrom_not_the_whole_file() {
        // A large "already-checkpointed" prefix (well past any TrailingBytes window a real guard
        // would use) followed by a few new lines. Before this fix, captureRawBytes always
        // allocated a buffer the size of the WHOLE file, every poll, no matter how little was new.
        var padding = string.Concat(Enumerable.Repeat("x", 50_000)); // one huge already-processed line
        var content = padding + "\nnew1\nnew2\nnew3\n";
        var path    = WriteTemp(content);
        var paddingLineByteLength = padding.Length + 1; // "xxxx...x\n"
        var totalFileLength       = (long)Encoding.UTF8.GetByteCount(content);

        var r = await ReadViaStreamRawBounded(
            path, linesProcessed: 1, rawBytesReadFrom: paddingLineByteLength, newRangeByteOffset: paddingLineByteLength);

        // Only the new lines are decoded, correctly labelled starting at linesProcessed.
        await Assert.That(r.Lines).IsEquivalentTo(new[] { "new1", "new2", "new3" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 1, 2, 3 });

        // The captured buffer starts at rawBytesReadFrom and is bounded by the NEW range —
        // nowhere near the ~50,000-byte padding line, which the old unconditional whole-file
        // capture would have re-read and re-hashed every single poll.
        await Assert.That(r.SnapshotStartOffset).IsEqualTo((long)paddingLineByteLength);
        await Assert.That(r.SnapshotBytes).IsNotNull();
        await Assert.That(r.SnapshotBytes!.Length).IsEqualTo((int)(totalFileLength - paddingLineByteLength));
        await Assert.That(r.SnapshotBytes!.Length).IsLessThan(1000); // "new1\nnew2\nnew3\n" — tiny
        await Assert.That(r.SnapshotByteLength).IsEqualTo(totalFileLength); // total file length unaffected
    }

    [Test]
    public async Task Bounded_capture_on_an_idle_poll_with_no_new_lines_allocates_essentially_nothing() {
        // The Cursor watcher's common case: a 1-second poll on a large, fully-synced transcript
        // with nothing new appended since the last poll. rawBytesReadFrom == newRangeByteOffset ==
        // EOF (mirrors DrainNewLines when cursorGuardOldOffset already equals the file length).
        var padding = string.Concat(Enumerable.Repeat("y", 100_000));
        var content = padding + "\n";
        var path    = WriteTemp(content);
        var totalFileLength = (long)Encoding.UTF8.GetByteCount(content);

        var r = await ReadViaStreamRawBounded(
            path, linesProcessed: 1, rawBytesReadFrom: totalFileLength, newRangeByteOffset: totalFileLength);

        await Assert.That(r.Lines).IsEmpty();
        await Assert.That(r.SnapshotBytes).IsNotNull();
        // Nothing new, no trailing-zone lookback requested (rawBytesReadFrom == EOF) — the
        // buffer allocated is ~0 bytes, not the ~100,000-byte file.
        await Assert.That(r.SnapshotBytes!.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Bounded_capture_snapshot_bytes_still_bind_exactly_to_the_new_range_decoded() {
        // Mirrors CaptureRawBytes_snapshot_bytes_are_exactly_the_bytes_that_decoded_the_returned_lines
        // for the BOUNDED path: the guard's exact-byte-binding property (the bytes hashed for the
        // new-range record are the SAME bytes decoded into the sent lines) must survive making the
        // capture incremental, not just the whole-file case.
        var padding = "prefix-line-already-checkpointed";
        var content = padding + "\nnew1\nnew2\n";
        var path    = WriteTemp(content);
        var paddingLineByteLength = Encoding.UTF8.GetByteCount(padding) + 1;

        var r = await ReadViaStreamRawBounded(
            path, linesProcessed: 1, rawBytesReadFrom: paddingLineByteLength, newRangeByteOffset: paddingLineByteLength);

        // Re-decoding SnapshotBytes independently (from its own start, offset 0 relative to the
        // snapshot itself) must reproduce exactly the lines the bounded read already decoded.
        var reDecoded = WatchCommand.SplitNewCompleteLines(
            Encoding.UTF8.GetString(r.SnapshotBytes!), 0, WatchCommand.IncompleteFinalLinePolicy.Hold);

        await Assert.That(reDecoded.Lines).IsEquivalentTo(r.Lines);
    }
}
