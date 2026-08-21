using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>
/// Per-session on-disk marker paths for the Cursor tailing watcher's runtime rewrite guard
/// (quarantine), ordering-sensitive hook side-effect barrier, and hook heartbeat. One
/// dot-namespaced directory per marker kind under the CLI's config dir (honouring
/// <c>KCAP_CONFIG_DIR</c> via <see cref="PathHelpers.ConfigPath"/>), one file per session keyed
/// by the dashless session id, so every process on this machine — hook, watcher, backfill,
/// import — resolves the same path. The barrier and heartbeat markers wrap
/// <see cref="WatcherHeartbeat"/>'s atomic timestamp read/write; the quarantine marker carries a
/// small JSON payload (reason + timestamp) instead, as a permanent diagnostic record rather than
/// a rolling liveness signal.
/// </summary>
public static class CursorMarkers {
    public static string QuarantinePath(string sessionId) => Path.Combine(PathHelpers.ConfigPath("cursor-quarantine"), $"{sessionId}.json");
    public static string BarrierPath(string sessionId)    => Path.Combine(PathHelpers.ConfigPath("cursor-barrier"), $"{sessionId}.json");
    public static string HeartbeatPath(string sessionId)  => Path.Combine(PathHelpers.ConfigPath("cursor-heartbeat"), $"{sessionId}.json");

    /// <summary>
    /// Per-CHILD marker path recording that this subagent's <c>subagent-start</c> was durably
    /// acknowledged (2xx), either via its own live POST or a later spool-drain replay. Keyed on
    /// the child's own dashless session id (not the parent) — mirrors
    /// <c>CursorLiveSubagentLinker</c>'s marker keying.
    /// </summary>
    public static string SubagentStartAckPath(string childSessionId) =>
        Path.Combine(PathHelpers.ConfigPath("cursor-subagent-start-ack"), $"{childSessionId}.json");

    /// <summary>
    /// The shared bound every <see cref="BarrierPending"/> caller (the backfill and the live
    /// watcher) uses to decide when a barrier has aged out. A single source of truth so the
    /// backfill and the watcher agree on how long they hold transcript delivery for the same
    /// session before proceeding past a crashed hook's uncleared barrier.
    /// </summary>
    public static readonly TimeSpan DefaultBarrierBound = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True once <see cref="Quarantine"/> has written a marker for this session. Fail-open on any
    /// read error (corrupt/partial marker, permission issue) — a broken quarantine check must
    /// never itself become the reason live capture stalls. Consulted by the backfill and import
    /// source in a later task.
    /// </summary>
    public static bool IsQuarantined(string sessionId) {
        try { return File.Exists(QuarantinePath(sessionId)); } catch { return false; }
    }

    /// <summary>
    /// Writes a timestamped quarantine marker for <paramref name="sessionId"/>, atomically
    /// (sibling temp file + rename — the <see cref="WatcherHeartbeat.Touch"/> precedent) so a
    /// concurrent reader never observes a partial write. Idempotent — a session quarantined twice
    /// keeps the FIRST reason/timestamp unless the existing marker is corrupt/unreadable, in which
    /// case it's replaced.
    ///
    /// Best-effort: this is called mid-drain-loop (<c>CursorRewriteGuard.Reject</c>), which
    /// has no broad exception handler above it, so any write failure here (permissions, disk full,
    /// read-only volume) must never throw — it's caught and logged instead. The caller's
    /// Verify*/Reject return value is what actually stops delivery; a lost marker only means a
    /// later process can't see this session was already quarantined.
    /// </summary>
    public static void Quarantine(string sessionId, string reason) {
        if (ReadMarker(sessionId) is not null) return; // already quarantined; keep the first reason

        try {
            var path = QuarantinePath(sessionId);
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var marker = new CursorQuarantineMarker(reason, DateTimeOffset.UtcNow);
            var json   = JsonSerializer.Serialize(marker, CapacitorJsonContext.Default.CursorQuarantineMarker);
            var tmp    = $"{path}.tmp";

            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        } catch (Exception e) {
            Console.Error.WriteLine($"cursor_quarantine_marker_write_failed: session {sessionId}: {e.Message}");
        }
    }

    /// <summary>The persisted quarantine marker for this session, or null if absent/corrupt.</summary>
    public static CursorQuarantineMarker? ReadMarker(string sessionId) {
        var path = QuarantinePath(sessionId);
        if (!File.Exists(path)) return null;

        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CursorQuarantineMarker);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // Corrupt/partial JSON, or a transient read error while a peer holds the file
            // exclusively mid-write — treat as "not quarantined yet" (MachineId.ReadPersisted
            // precedent), never throw out of a guard check.
            return null;
        }
    }

    /// <summary>
    /// Creates (or refreshes) the per-session side-effect barrier at <paramref name="now"/>.
    /// Created BEFORE the POST of an ordering-sensitive hook whose
    /// server-side effect must land before transcript-line normalization can consume it
    /// (today: <c>beforeSubmitPrompt</c> → <c>user-prompt/cursor</c>, which queues an
    /// attachment the Cursor normalizer later attaches to the matching user transcript line).
    /// Reuses <see cref="WatcherHeartbeat.Touch"/>'s atomic sibling-temp-then-rename write —
    /// the barrier is just a timestamp marker, same shape as a heartbeat.
    /// </summary>
    public static void CreateBarrier(string sessionId, DateTimeOffset now) =>
        WatcherHeartbeat.Touch(BarrierPath(sessionId), now);

    /// <summary>
    /// True while a barrier created for <paramref name="sessionId"/> is still within
    /// <paramref name="bound"/> of its creation timestamp — while true, the watcher and the
    /// backfill (wired in later tasks) HOLD transcript delivery for this session rather than
    /// risk normalizing a line ahead of the attachment it depends on. False when no barrier
    /// was ever created (or one already <see cref="ClearBarrier"/>ed), and false once
    /// <paramref name="now"/> has moved <paramref name="bound"/> past the recorded timestamp —
    /// a hook that crashed before clearing its own barrier must not wedge delivery forever;
    /// past the bound, delivery proceeds (a bounded, deliberate attachment loss).
    /// </summary>
    public static bool BarrierPending(string sessionId, DateTimeOffset now, TimeSpan bound) {
        var stamp = WatcherHeartbeat.Read(BarrierPath(sessionId));
        return stamp is { } s && now - s < bound;
    }

    /// <summary>
    /// Clears the barrier — called on a 2xx response from the ordering-sensitive hook's own
    /// live POST, or when a later invocation's hook-spool drain delivers that same spooled
    /// entry. Best-effort: an absent marker (nothing to clear) is not an error.
    /// </summary>
    public static void ClearBarrier(string sessionId) {
        try { File.Delete(BarrierPath(sessionId)); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Touches the per-session hook heartbeat at <paramref name="now"/>. Called at the top of
    /// EVERY <c>Capacitor.Cli.Commands.CursorHookCommand</c>
    /// invocation that carries a session id — including telemetry-only hooks — so the
    /// heartbeat reflects "Cursor is still firing hooks for this session" independent of
    /// whether the tailing watcher is itself alive. Reuses <see cref="WatcherHeartbeat.Touch"/>.
    /// </summary>
    public static void TouchHeartbeat(string sessionId, DateTimeOffset now) =>
        WatcherHeartbeat.Touch(HeartbeatPath(sessionId), now);

    /// <summary>
    /// True once a positive (2xx) acknowledgement of this child's <c>subagent-start</c> has been
    /// recorded. Fail-open on any read error (an unreadable marker
    /// is treated as "not acked yet", never as a crash) — consulted by
    /// <c>CursorHookCommand.HandleSubagentChildEventAsync</c> before any content-less backfill,
    /// the child's own <c>subagent-stop</c>, or the child watcher spawn: none of those may ever
    /// run without this marker, so a permanently-dropped spooled start (non-transient 4xx —
    /// <see cref="HookSpool"/>'s <c>DrainOutcome.Drop</c>) can never open an ungated transcript
    /// path for a subagent whose <c>SubagentStarted</c> was never appended server-side.
    /// </summary>
    public static bool HasSubagentStartAck(string childSessionId) {
        try { return File.Exists(SubagentStartAckPath(childSessionId)); } catch { return false; }
    }

    /// <summary>
    /// Marks <paramref name="childSessionId"/>'s <c>subagent-start</c> as durably acknowledged.
    /// Called the moment a 2xx is observed for that entry — either the
    /// live POST in <c>HandleSubagentChildEventAsync</c> or a later spool-drain delivery in
    /// <c>CursorHookCommand.MaybeSpawnChildWatcherFromPayloadAsync</c> — so every subsequent hook
    /// invocation for this child (a fresh process each time) can see the ack without re-deriving
    /// it. Reuses <see cref="WatcherHeartbeat.Touch"/>'s atomic write; the content is unused, only
    /// the marker's presence matters.
    /// </summary>
    public static void MarkSubagentStartAcked(string childSessionId) {
        try { WatcherHeartbeat.Touch(SubagentStartAckPath(childSessionId), DateTimeOffset.UtcNow); }
        catch { /* best-effort — see HasSubagentStartAck's fail-open contract */ }
    }
}

/// <summary>Quarantine marker persisted to <see cref="CursorMarkers.QuarantinePath"/>.</summary>
public readonly record struct CursorQuarantineMarker(string Reason, DateTimeOffset QuarantinedAt);
