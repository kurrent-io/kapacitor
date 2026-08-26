using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core;

/// <summary>
/// Global, session-agnostic drain pass run at the start of every kcap invocation (and periodically by
/// the daemon). Enforces cross-spool ordering per session: spooled session-start → transcript tail
/// (+ needs-import marker) → spooled session-end. Idempotent under repeated/partial passes via
/// deterministic server ids. Stdout-contract hooks (Codex) run this in the BACKGROUND / AFTER stdout.
///
/// <para>Moved to <c>Capacitor.Cli.Core</c> so the daemon's periodic drain can share
/// this exact ordering logic without referencing the CLI's exe project.</para>
/// </summary>
public static class LifecycleSpoolDrain {
    // Injectable core (test seam). Ordering is enforced by draining the lifecycle spool's START routes
    // first, then the transcript spool, then the lifecycle END routes — per session.
    public static async Task RunAsync(
            HookSpool lifecycle, TranscriptSpool transcript, string? currentSessionId,
            Func<string, string, Task<DrainOutcome>> lifecyclePoster,
            Func<string, Task<DrainOutcome>>         transcriptPoster,
            TimeSpan budget, CancellationToken ct) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool Expired() => sw.Elapsed >= budget || ct.IsCancellationRequested;

        foreach (var sid in OrderedSessions(lifecycle, transcript, currentSessionId)) {
            if (Expired()) return;

            // Task 12 / BLOCKER-3: a session whose terminal (session-end) route was already
            // durably delivered by a PRIOR pass must never have a straggler non-terminal entry
            // (e.g. a late subagent-stop) delivered after it — that would be a real ordering
            // violation, reachable now that session-start/subagent-stop/session-end share one spool
            // file. Drop the remainder instead of replaying it out of order.
            if (lifecycle.IsMarkedEnded(sid)) {
                lifecycle.DiscardRemainder(sid);
                continue;
            }

            // Phase 1: session-start (and any non-terminal lifecycle) BEFORE transcript tail.
            await lifecycle.DrainRoutesAsync(sid, isTerminal: false, lifecyclePoster, Expired, ct);
            if (Expired()) return;
            // Phase 2: transcript tail. If it can't fully drain, DO NOT drain session-end (ordering).
            await transcript.DrainAsync(sid, transcriptPoster, Expired, ct);
            if (transcript.HasBacklog(sid)) continue;
            if (transcript.NeedsImport(sid)) await DeliverNeedsImportAsync(sid, lifecyclePoster);
            if (Expired()) return;
            // Phase 3: session-end LAST. A non-zero consumed count means a terminal entry was truly
            // resolved (delivered or permanently dropped) in THIS call — not merely left for a retry
            // by a TransientStop/budget — so the session can be durably marked ended.
            var consumed = await lifecycle.DrainRoutesAsync(sid, isTerminal: true, lifecyclePoster, Expired, ct);
            if (consumed > 0) lifecycle.MarkEnded(sid);
        }
    }

    static async Task DeliverNeedsImportAsync(string sid, Func<string, string, Task<DrainOutcome>> poster) {
        var body = new System.Text.Json.Nodes.JsonObject { ["session_id"] = sid, ["needs_import"] = true }.ToJsonString();
        await poster("session-needs-import", body); // route resolved server-side; best-effort
    }

    static IEnumerable<string> OrderedSessions(HookSpool life, TranscriptSpool tx, string? current) {
        var ids = new List<string>();
        void Add(string? s) { if (s is not null && !ids.Contains(s)) ids.Add(s); }
        Add(current);
        foreach (var s in life.SessionIdsWithBacklog()) Add(s);
        foreach (var s in tx.SessionIdsWithBacklog()) Add(s);
        return ids;
    }

    /// <summary>
    /// Production wrapper: real HTTP posters that map status → DrainOutcome (mirrors ClaudeHookCommand's
    /// ClaudePoster / CursorHookCommand's PostOnce-based poster).
    ///
    /// <para><b>generate_whats_done.</b> <paramref name="onWhatsDoneRequested"/>
    /// is invoked with <c>(sessionId, vendor)</c> whenever a terminal (session-end) entry this drain
    /// delivers comes back with <c>generate_whats_done: true</c> — the exact side effect
    /// <c>ClaudeHookCommand.ClaudePoster</c> performs for Claude's own session-end replay. Without this,
    /// a generic drain of a spooled session-end (from ANY vendor — the server signals
    /// <c>generate_whats_done</c> vendor-agnostically, see <c>WatchCommand</c>'s parent-exit path) would
    /// silently drop the what's-done generation. Callers that don't need it (or can't reach the
    /// process-spawning helper — the daemon has its own) may omit it.</para>
    /// </summary>
    public static Task RunAsync(CursorMarkers markers, HttpClient client, string baseUrl, HookSpool lifecycle,
                                TranscriptSpool transcript, string? currentSessionId, TimeSpan budget,
                                CancellationToken ct, Action<string, string>? onWhatsDoneRequested = null)
        => RunAsync(lifecycle, transcript, currentSessionId,
            lifecyclePoster: (route, body) => PostOnce(client, baseUrl, route, body, ct, onWhatsDoneRequested),
            transcriptPoster: body => PostTranscript(markers, client, baseUrl, body, ct),
            budget, ct);

    static async Task<DrainOutcome> PostOnce(
            HttpClient client, string baseUrl, string route, string body, CancellationToken ct,
            Action<string, string>? onWhatsDoneRequested) {
        try {
            using var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostOnceAsync($"{baseUrl}/hooks/{route}", content, TimeSpan.FromSeconds(3), ct);
            if (resp.IsSuccessStatusCode) {
                if (HookSpool.IsTerminalRoute(route) && onWhatsDoneRequested is not null) {
                    try {
                        var respNode = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                        var sid      = System.Text.Json.Nodes.JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
                        if (respNode?["generate_whats_done"]?.GetValue<bool>() == true && sid is not null)
                            onWhatsDoneRequested(sid, VendorOf(route));
                    } catch { /* best effort — never fail the drain over this */ }
                }
                return DrainOutcome.Delivered;
            }
            var code = (int)resp.StatusCode;
            return code is >= 500 or 408 or 429 ? DrainOutcome.TransientStop : DrainOutcome.Drop;
        } catch { return DrainOutcome.TransientStop; }
    }

    // "session-end/kiro" → "kiro"; literal "session-end" (Claude's own naming, no vendor suffix) →
    // "claude", matching ClaudePoster's implicit default.
    static string VendorOf(string route) {
        var slash = route.IndexOf('/');
        return slash >= 0 ? route[(slash + 1)..] : "claude";
    }

    static Task<DrainOutcome> PostTranscript(
            CursorMarkers markers, HttpClient client, string baseUrl, string body, CancellationToken ct) {
        // review fix #1/#8 — a Cursor batch already quarantined by the runtime rewrite
        // guard must never be replayed from the shutdown transcript spool: the tail spooled at
        // shutdown could predate the quarantine (a batch queued before the guard tripped on a
        // later poll), and this is the ONLY delivery-time check the spool-replay path has, since
        // the live watcher's own checks never see a batch once it's on disk here. Drop it
        // permanently (D0's quarantine is a deliberate, non-retracted stop — see
        // CursorRewriteGuard) rather than post the corrupted tail behind the watcher's back. A
        // pending side-effect barrier is treated as transient (retry the whole pass later) since
        // it self-clears/expires.
        try {
            var node   = System.Text.Json.Nodes.JsonNode.Parse(body);
            var vendor = node?["vendor"]?.GetValue<string>();
            var sid    = node?["session_id"]?.GetValue<string>();

            if (vendor == "cursor" && sid is not null) {
                if (markers.IsQuarantined(sid)) return Task.FromResult(DrainOutcome.Drop);
                if (markers.BarrierPending(sid, DateTimeOffset.UtcNow, CursorMarkers.DefaultBarrierBound))
                    return Task.FromResult(DrainOutcome.TransientStop);
            }
        } catch {
            // Malformed body (shouldn't happen — this is always our own serialized
            // TranscriptBatch) — fall through to the normal post rather than risk stalling the
            // drain over a marker check.
        }

        return PostOnce(client, baseUrl, "transcript", body, ct, onWhatsDoneRequested: null);
    }
}
