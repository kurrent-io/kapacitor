using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// One-shot transcript-line backfill. Reads the shared transcript watermark
/// for <c>sessionId</c> (<c>GET /api/sessions/{sid}/last-line</c>
/// — the same route every transcript-driven normalizer uses), opens the
/// JSONL transcript file, and POSTs every line past the watermark as a
/// single batch to <c>POST /hooks/transcript</c> with
/// <c>Vendor: "cursor"</c>. No internal retry — the next hook invocation
/// re-reads the (advanced) server watermark and resumes from the new HWM.
/// </summary>
public static class CursorTranscriptBackfill {
    static readonly TimeSpan WatermarkTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan BatchPostTimeout = TimeSpan.FromMilliseconds(1500);

    public readonly record struct Stats(int LinesPosted, bool Failed);

    /// <param name="agentId">
    /// when set, <paramref name="sessionId"/> is the
    /// PARENT session and the batch is routed to its <c>AgentSubsession-{sessionId}-{agentId}</c>
    /// stream instead of the top-level <c>AgentSession-{sessionId}</c> — mirroring the watermark
    /// probe + transcript POST shape <c>CursorImportSource.SendSubagentLifecycleAsync</c> uses for
    /// historical import, so live and import converge on the same subsession watermark.
    /// </param>
    /// <param name="finalDrain">
    /// Task 10 (D2) — set ONLY by the <c>sessionEnd</c> pre-end drain. Selects
    /// <see cref="WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete"/> instead of the
    /// default <see cref="WatchCommand.IncompleteFinalLinePolicy.Hold"/>: at session end the
    /// hook (not the live watcher) is the last component that can ever observe this transcript,
    /// so a valid newline-less final record must be consumed rather than permanently stranded.
    /// </param>
    public static async Task<Stats> RunAsync(
            CursorMarkers     markers,
            HttpClient        client,
            string            baseUrl,
            string            sessionId,
            string?           transcriptPath,
            Func<bool>        budget,
            CancellationToken ct,
            string?           agentId    = null,
            bool              finalDrain = false
        ) {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) {
            return new Stats(0, false);
        }

        // a session already quarantined by the runtime rewrite guard must never
        // have more transcript lines delivered; the watcher has already given up on it.
        if (markers.IsQuarantined(sessionId)) {
            return new Stats(0, false);
        }

        // an ordering-sensitive hook (beforeSubmitPrompt) may have queued an
        // attachment the Cursor normalizer needs to see BEFORE the matching user transcript line
        // is normalized. While the barrier is pending, hold delivery entirely (retry next
        // invocation) rather than risk normalizing ahead of the attachment.
        if (markers.BarrierPending(sessionId, DateTimeOffset.UtcNow, CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false);
        }

        int resumeFrom;

        var watermarkUrl = string.IsNullOrEmpty(agentId)
            ? $"{baseUrl}/api/sessions/{sessionId}/last-line"
            : $"{baseUrl}/api/sessions/{sessionId}/last-line?agentId={Uri.EscapeDataString(agentId)}";

        try {
            using var resp = await client.GetOnceAsync(
                watermarkUrl,
                WatermarkTimeout,
                ct
            );

            // 200 — body has last_line_number; 204 — stream exists but no
            // lines yet (resume from 0); 404 — stream doesn't exist (resume
            // from 0); any other non-2xx — fail-open, retry next hook.
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) {
                resumeFrom = 0;
            } else if (!resp.IsSuccessStatusCode) {
                return new Stats(0, Failed: true);
            } else {
                var       body = await resp.Content.ReadAsStringAsync(ct);
                using var doc  = JsonDocument.Parse(body);

                resumeFrom = doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                    ? ln.GetInt32() + 1
                    : 0;
            }
        } catch { return new Stats(0, Failed: true); }

        // Read every line past the watermark into the batch, via the same length-capped,
        // concurrent-append-safe primitive the live watcher uses (Task 10 — replaces
        // the prior ad-hoc StreamReader loop, which held nothing back for a still-being-written
        // final line and so risked truncating it). Cursor's JSONL is bounded by the agent turn
        // count — practical sizes are dozens of lines, not thousands; the server's
        // HandleTranscript ingests them in one shot.
        List<string> lines;
        List<int>    lineNumbers;

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var policy = finalDrain
                ? WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete
                : WatchCommand.IncompleteFinalLinePolicy.Hold;
            var read = await WatchCommand.ReadNewCompleteLinesAsync(stream, resumeFrom, policy, ct);

            lines       = read.Lines.Select(SecretRedactor.RedactLine).ToList();
            lineNumbers = read.LineNumbers;
        } catch { return new(0, Failed: true); }

        if (lines.Count == 0 || budget()) return new(0, Failed: false);

        // re-check both markers IMMEDIATELY at the delivery boundary,
        // not only before the watermark GET + file read above. A concurrent beforeSubmitPrompt
        // (creating its barrier) or a guard trip on the live watcher (quarantining the session)
        // landing in that window — between the early check and this POST — must still be caught
        // here rather than let the transcript line overtake the attachment it depends on, or
        // escape the quarantine the watcher just imposed.
        if (markers.IsQuarantined(sessionId)
         || markers.BarrierPending(sessionId, DateTimeOffset.UtcNow, CursorMarkers.DefaultBarrierBound)) {
            return new Stats(0, false);
        }

        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [..lines],
            LineNumbers = [..lineNumbers],
            Vendor      = "cursor",
        };

        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

        HttpResponseMessage? resp2 = null;

        try {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            resp2 = await client.PostOnceAsync(
                $"{baseUrl}/hooks/transcript",
                content,
                BatchPostTimeout,
                ct
            );

            return resp2.IsSuccessStatusCode ? new(lines.Count, Failed: false) : new Stats(0, Failed: true);
        } catch {
            return new Stats(0, Failed: true);
        } finally {
            resp2?.Dispose();
        }
    }
}
