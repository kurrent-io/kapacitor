using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// Runtime two-zone rewrite guard for the per-session Cursor tailing watcher (defence-in-depth —
/// see <see cref="CursorVerifyAppendOnlyCommand"/>). Cursor's JSONL transcript is expected to be
/// append-only, but nothing in the IDE's contract guarantees it, and a silent in-place rewrite
/// would corrupt the server's source-acknowledgement frontier by re-sending stale byte ranges
/// under line numbers it already disposed of.
///
/// Two zones are checked on every poll before a batch is sent:
/// <list type="bullet">
/// <item><b>Prior zone</b> — the trailing <see cref="TrailingBytes"/> bytes ending at the last
/// committed checkpoint offset (<see cref="Checkpoint"/>). <see cref="HashPriorZone(ReadOnlySpan{byte}, long)"/> +
/// <see cref="VerifyPriorZone"/> must be called both immediately before and immediately after the
/// length-capped snapshot read of the new range, so a rewrite racing the read itself can't slip
/// through a single-snapshot check.</item>
/// <item><b>New range</b> — the bytes just read for the batch about to be sent.
/// <see cref="RecordNewRangeRead"/> hashes them at read time; <see cref="VerifyNewRange"/>
/// re-hashes the SAME capped byte range re-read immediately before send and compares.</item>
/// </list>
///
/// A length shrink or any hash mismatch in either zone is a structured
/// <c>cursor_transcript_rewrite_detected</c> diagnostic: the caller must discard the unsent batch
/// and exit; this guard itself writes the per-session
/// <see cref="CursorMarkers.Quarantine">quarantine marker</see> the moment it detects one, so the
/// decision survives the process exiting. <see cref="VerifyFullPrefix"/> is the coarser-grained
/// periodic full-prefix re-hash (every N polls, wired by the watcher), reusing
/// <see cref="CursorAppendOnlyProbe"/> — the same pure core the phase-0 harness uses.
/// </summary>
public sealed class CursorRewriteGuard(ConfigRoot config, string sessionId) {
    readonly CursorMarkers _markers = new(config);

    public int TrailingBytes { get; init; } = 4096;

    (long Offset, string TrailingSha)?                _checkpoint;
    (long OldOffset, long SampledLength, string Sha)?  _pendingNewRange;
    CursorAppendOnlyProbe.Sample?                      _prefixSample;

    /// <summary>Records the last-sent batch's end offset and the sha256 of the trailing
    /// <see cref="TrailingBytes"/> bytes ending there, for the prior-zone check on the NEXT poll.</summary>
    public void Checkpoint(long offset, string trailingSha) => _checkpoint = (offset, trailingSha);

    /// <summary>
    /// A shrink (the file is now SHORTER than <paramref name="checkpointOffset"/>, the last
    /// committed checkpoint) is unambiguous evidence of a rewrite on its own, independent of every
    /// other zone check. True (no trip) when the file hasn't shrunk below the checkpoint. On a
    /// trip, writes the structured diagnostic and quarantines the session, like every other
    /// Verify* method here.
    /// </summary>
    public bool VerifyNotShrunk(long newLength, long checkpointOffset) {
        if (newLength >= checkpointOffset) return true;

        Reject("shrink", $"file length {newLength} is now shorter than the last checkpoint offset {checkpointOffset}");

        return false;
    }

    /// <summary>
    /// Hashes the trailing <see cref="TrailingBytes"/> bytes ending at the current checkpoint
    /// offset in <paramref name="stream"/>, without disturbing its position. Call once before and
    /// once after the new-range snapshot read, feeding each result to <see cref="VerifyPriorZone"/>.
    /// </summary>
    public string HashPriorZone(FileStream stream) {
        if (_checkpoint is not { } cp) return "";

        var savedPosition = stream.Position;

        try {
            var start  = Math.Max(0, cp.Offset - TrailingBytes);
            var length = (int)(cp.Offset - start);

            if (length <= 0) return CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty);

            var buffer = new byte[length];
            stream.Position = start;
            var read = ReadFully(stream, buffer);

            return CursorAppendOnlyProbe.Sha256Hex(buffer.AsSpan(0, read));
        } finally {
            stream.Position = savedPosition;
        }
    }

    /// <summary>
    /// Snapshot-based counterpart to <see cref="HashPriorZone(FileStream)"/>. Hashes the trailing
    /// <see cref="TrailingBytes"/> bytes ending at the current checkpoint offset directly from
    /// <paramref name="snapshot"/> — an in-memory buffer captured during the SAME capped read that
    /// decoded the batch about to be sent, starting at absolute file offset
    /// <paramref name="snapshotStartOffset"/> (0 when the caller captured from the true file
    /// start). Binding the prior-zone hash to that snapshot, rather than a fresh disk reopen taken
    /// moments later, closes a TOCTOU window: a rewrite landing between the decode read and a
    /// later reopen could otherwise produce a hash that stayed "stable" for the rest of the poll
    /// without ever corresponding to the bytes the batch was actually built from.
    ///
    /// <paramref name="snapshotStartOffset"/> lets the caller pass a snapshot that starts PARTWAY
    /// through the file (a bounded read covering only the prior-tail zone + new range, not the
    /// whole file) instead of always materializing everything from byte 0 — the window this
    /// method needs, [checkpoint - TrailingBytes, checkpoint), is clipped to whatever the
    /// snapshot actually covers.
    /// </summary>
    public string HashPriorZone(ReadOnlySpan<byte> snapshot, long snapshotStartOffset = 0) {
        if (_checkpoint is not { } cp) return "";

        var start = Math.Max(0, cp.Offset - TrailingBytes);
        var end   = cp.Offset;

        // Clip to the region this snapshot actually covers.
        var clippedStart = Math.Max(start, snapshotStartOffset);
        var clippedEnd   = Math.Min(end, snapshotStartOffset + snapshot.Length);

        if (clippedEnd <= clippedStart) return CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty);

        return CursorAppendOnlyProbe.Sha256Hex(
            snapshot[(int)(clippedStart - snapshotStartOffset)..(int)(clippedEnd - snapshotStartOffset)]);
    }

    /// <summary>
    /// Resets the guard's checkpoint after a reconnect rewind discovers the server's acknowledged
    /// frontier is behind the client's own line cursor. The two-zone checks resume from a clean
    /// slate, like a fresh watcher's very first poll — leaving the stale, later checkpoint in
    /// place would start new-range verification past the bytes the replayed line gap actually
    /// occupies, so a rewrite landing inside that gap could ship unnoticed.
    /// </summary>
    public void ResetCheckpoint() => _checkpoint = null;

    /// <summary>
    /// True when <paramref name="currentTrailingSha"/> (from <see cref="HashPriorZone(ReadOnlySpan{byte}, long)"/>) still
    /// matches the stored checkpoint hash. Always true when no checkpoint has been recorded yet
    /// (nothing to compare against on the very first batch). On a mismatch, writes the structured
    /// diagnostic and quarantines the session.
    /// </summary>
    public bool VerifyPriorZone(string currentTrailingSha) {
        if (_checkpoint is not { } cp) return true;

        var ok = string.Equals(currentTrailingSha, cp.TrailingSha, StringComparison.Ordinal);

        if (!ok) {
            Reject("prior_zone", $"trailing-{TrailingBytes}-byte hash at checkpoint offset {cp.Offset} no longer reproduces");
        }

        return ok;
    }

    /// <summary>Hashes and stores the bytes just read for the range <c>(oldOffset, oldOffset + sampledLength]</c>.</summary>
    public void RecordNewRangeRead(long oldOffset, long sampledLength, ReadOnlySpan<byte> readBytes) =>
        _pendingNewRange = (oldOffset, sampledLength, CursorAppendOnlyProbe.Sha256Hex(readBytes));

    /// <summary>
    /// Re-hashes <paramref name="readBytes"/> — the SAME capped range re-read from disk
    /// immediately before send — and compares against the hash <see cref="RecordNewRangeRead"/>
    /// took moments earlier. A length shrink (fewer bytes than were originally sampled) or a hash
    /// mismatch means the range was rewritten in place between read and send; either writes the
    /// structured diagnostic and quarantines the session.
    /// </summary>
    public bool VerifyNewRange(ReadOnlySpan<byte> readBytes, long oldOffset, long sampledLength) {
        if (_pendingNewRange is not { } pending || pending.OldOffset != oldOffset || pending.SampledLength != sampledLength) {
            // No matching prior read recorded for this exact range — nothing to compare yet.
            // Callers always Record then Verify; record now defensively so a genuine future
            // mismatch (not this bookkeeping edge) is what trips the guard.
            RecordNewRangeRead(oldOffset, sampledLength, readBytes);

            return true;
        }

        if (readBytes.Length < sampledLength) {
            Reject("new_range", $"re-read at offset {oldOffset} produced {readBytes.Length} bytes, fewer than the {sampledLength} originally sampled (shrink)");

            return false;
        }

        var ok = string.Equals(CursorAppendOnlyProbe.Sha256Hex(readBytes), pending.Sha, StringComparison.Ordinal);

        if (!ok) {
            Reject("new_range", $"re-hash of byte range (offset {oldOffset}, length {sampledLength}) no longer matches the hash taken when it was first read");
        }

        return ok;
    }

    /// <summary>
    /// Periodic full-prefix re-hash (every N polls — cadence owned by the watcher). Seeds on the
    /// first call (nothing to compare against yet); every call after that runs
    /// <see cref="CursorAppendOnlyProbe.PrefixStable"/> against the previous sample, quarantining
    /// on a mismatch.
    /// </summary>
    public bool VerifyFullPrefix(CursorAppendOnlyProbe.Sample later, ReadOnlySpan<byte> laterBytes) {
        if (_prefixSample is not { } earlier) {
            _prefixSample = later;

            return true;
        }

        var ok = CursorAppendOnlyProbe.PrefixStable(earlier, later, laterBytes);

        if (!ok) {
            Reject("full_prefix", $"prefix hash at length {earlier.Length} no longer reproduces from a length-{later.Length} full re-read");
        }

        _prefixSample = later;

        return ok;
    }

    void Reject(string zone, string detail) {
        var message = $"cursor_transcript_rewrite_detected: session {sessionId} zone={zone} — {detail}";
        Console.Error.WriteLine(message);
        _markers.Quarantine(sessionId, message);
    }

    static int ReadFully(FileStream stream, byte[] buffer) {
        var total = 0;

        while (total < buffer.Length) {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }

        return total;
    }
}
