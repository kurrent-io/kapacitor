using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>
/// Discover + classify + import historical Google Antigravity conversations.
/// Each conversation is a brain dir under
/// <c>~/.gemini/antigravity/brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c> —
/// the same lines the live watcher tails — so historical and live import converge on the
/// server's <c>AntigravityTranscriptNormalizer</c> (routed phase, like Gemini).
///
/// <para>The conversation id is the brain dir name (a dashed UUID). The session id we surface
/// is its DASHLESS canonical form — matching live capture (the Antigravity hook and
/// <c>kcap watch</c> strip dashes) so a session captured live and later re-imported dedupes to
/// one stream, and so <c>kcap import --antigravity --session &lt;id&gt;</c> filtering is
/// format-insensitive. The dashed conversation id is kept only for filesystem paths
/// (the brain-dir transcript). Subagents are separate conversations linked via
/// <c>INVOKE_SUBAGENT</c> steps in the parent's transcript (the spawn-time signal;
/// see <see cref="AntigravitySubagents"/>); roots are conversations
/// that are never a child, and their children are imported as subagents under them — each with
/// the DASHLESS child conversation id as its <c>agent_id</c> (the server canonicalizes agent_id
/// to dashless on both ingest and watermark read, so this matches live routing; mirrors
/// <c>GeminiImportSource</c>). Antigravity records no machine-readable workspace path in the
/// transcript, so import leaves <c>cwd</c> null (no repo enrichment / exclusion on historical
/// import — a v1 limitation shared with Gemini; live capture gets cwd from the hook payload).</para>
///
/// <para>Token/model cost lives off-transcript in each conversation's <c>gen_metadata</c> db; the
/// live path streams it as synthetic USAGE lines and import injects it via
/// <see cref="PostUsageLinesAsync"/>. **Subagents are the exception**: each child is its own
/// conversation with its own db, and <see cref="ImportChildrenAsync"/> posts child content without a
/// usage pass, so imported subagents carry content but not cost.</para>
/// </summary>
internal sealed class AntigravityImportSource : IImportSource {
    static readonly HashSet<string> RelevantTypes = new(StringComparer.Ordinal) {
        "USER_INPUT", "PLANNER_RESPONSE", "RUN_COMMAND", "VIEW_FILE", "LIST_DIRECTORY", "CODE_ACTION"
    };

    readonly AntigravityPaths _paths;

    public AntigravityImportSource(AntigravityPaths paths) => _paths = paths;

    // Both product roots' brain dirs (GUI + agy CLI), in fixed order. Import enumerates every one
    // that exists — an agy-only machine has only the CLI root, and before this it was invisible.
    IReadOnlyList<string> BrainRoots =>
        _paths.BrainProductRoots.Select(r => Path.Combine(r, "brain")).ToList();

    public HarnessId Vendor => HarnessId.Antigravity;
    public bool   IsAvailable => BrainRoots.Any(Directory.Exists);
    public bool   SupportsTitleGeneration => false; // server computes a fallback title at session-end
    public bool   AttachesChildContentOnReplay => true;  // AlreadyLoaded repair branch imports children

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [antigravity-import] {message}");

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var result = new List<DiscoveredSession>();

        // Normalize the --session filter to the dashless canonical form so it matches the
        // dashless session id we surface, whether the user passed a dashed or dashless id
        // (mirrors GeminiImportSource).
        var sessionFilter = filters.FilterSession is { } fs ? ImportCommand.NormalizeGuid(fs) : null;

        var sinceUtc = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        // Each product root is scanned independently: chains never cross roots (a child's
        // messages/ linkage lives beside its parent under one root) and conversation ids are
        // unique UUIDs, so results concatenate with no cross-root dedup.
        foreach (var brainRoot in BrainRoots) {
            ct.ThrowIfCancellationRequested();
            DiscoverInRoot(brainRoot, sessionFilter, sinceUtc, result, ct);
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    void DiscoverInRoot(
            string brainRoot, string? sessionFilter, DateTimeOffset? sinceUtc,
            List<DiscoveredSession> result, CancellationToken ct) {
        if (!Directory.Exists(brainRoot)) return;

        // The product root this brain dir belongs to (brainRoot is "<productRoot>/brain"), carried
        // onto each session so ImportChildrenAsync resolves child transcripts under the SAME root.
        var productRoot = Path.GetDirectoryName(brainRoot)!;

        // Resolve every conversation to its top-level ancestor: roots (no parent) import as
        // sessions; ALL transitive descendants (children, grandchildren, …) import as their
        // root's subagents. Cycle-/non-tree-safe (BuildRootDescendants) so a deep chain isn't
        // lost and a cycle imports standalone rather than vanishing. Linkage is complete on disk.
        var parentMap = AntigravitySubagents.BuildParentMapUnder(brainRoot, ct);
        List<string> convIds;
        try {
            convIds = Directory.EnumerateDirectories(brainRoot).Select(Path.GetFileName).OfType<string>().ToList();
        } catch {
            // A hostile/inaccessible brain root must not abort the whole import pass.
            convIds = [];
        }
        var byRoot    = AntigravitySubagents.BuildRootDescendants(convIds, parentMap);

        // drift observability: surface format drift without a messages fallback.
        // HashSet membership so the counters stay O(n), not O(n²), on large brain trees (qodo #285).
        var allConversationIds = convIds.ToHashSet(StringComparer.Ordinal);
        var linkedChildIds     = parentMap.Values.ToHashSet(StringComparer.Ordinal);
        var invokeEdges   = parentMap.Count;
        var danglingChild = parentMap.Keys.Count(c => !allConversationIds.Contains(c));
        var msgButNoInvoke = convIds.Count(id =>
            Directory.Exists(_paths.MessagesDirUnder(productRoot, id))
            && !parentMap.ContainsKey(id) && !linkedChildIds.Contains(id));
        Log($"Antigravity import ({Path.GetFileName(productRoot)}): {invokeEdges} invoke edge(s); {danglingChild} invoked child id(s) with no conversation dir; {msgButNoInvoke} conversation(s) with messages/ but no invoke edge");

        foreach (var (convId, descendants) in byRoot) {
            ct.ThrowIfCancellationRequested();

            // Dashless canonical id — matches live capture and fixes format-sensitive --session
            // filtering. The dashed convId still resolves the on-disk brain-dir transcript path.
            var sessionId = ImportCommand.NormalizeGuid(convId);

            if (sessionFilter is not null && !string.Equals(sessionId, sessionFilter, StringComparison.Ordinal)) continue;

            var transcript = _paths.TranscriptFullPathUnder(productRoot, convId);
            if (!File.Exists(transcript)) continue;

            var firstTimestamp = ReadFirstTimestamp(transcript);
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(transcript); } catch { /* best effort */ }
            }
            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      sessionId,
                Vendor:         Vendor,
                Cwd:            null,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"] = transcript,
                    // The product root this session lives under, so ImportChildrenAsync resolves
                    // its children under the same root rather than defaulting to the GUI's.
                    ["ProductRoot"]    = productRoot,
                    // Dashed conversation ids — kept dashed because they resolve on-disk brain-dir
                    // transcript paths in ImportChildrenAsync; the server-facing agent_id is
                    // canonicalized to dashless there.
                    ["Children"]       = descendants, // all transitive descendants, nested under this root
                }));
        }
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp };

            int? lastNonBlank, lastRelevant; int nonBlank;
            try {
                (lastNonBlank, lastRelevant, nonBlank) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
                continue;
            }

            if (lastNonBlank is null) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript"));
                continue;
            }
            if (nonBlank < ctx.MinLines) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlank));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, agentId: null, ct);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlank, "watermark probe failed"));
                continue;
            }

            meta.LastTimestamp = TryGetLastWriteUtc(transcriptPath);

            var status = ImportCommand.ClassificationStatus.New;
            var resume = 0;
            // Compare against the last IMPORT-RELEVANT line: Antigravity transcripts end with
            // normalizer-skipped steps (CHECKPOINT / GENERIC / SYSTEM_*), so a raw-tail compare
            // would re-classify a complete session Partial forever (mirrors Gemini).
            var lastImportable = lastRelevant ?? lastNonBlank.Value;
            if (serverLastLine is { } srv) {
                if (srv >= lastImportable) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                else { status = ImportCommand.ClassificationStatus.Partial; resume = srv + 1; }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId      = s.SessionId,
                FilePath       = "", // routed phase
                EncodedCwd     = "",
                Meta           = meta,
                Status         = status,
                Vendor         = Vendor,
                ResumeFromLine = resume,
                TotalLines     = nonBlank,
                SourceMeta     = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
        var transcriptPath = (string)c.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        // An AlreadyLoaded root's parent transcript is fully imported, but a prior run may have
        // advanced the transcript watermark and then FAILED a lifecycle POST — most damagingly
        // session-end, leaving the session with no end event and stuck "running" (the HWM tracks
        // only content lines, not lifecycle). The import pipeline routes AlreadyLoaded here
        // precisely so a source can re-assert lifecycle. Re-post session-start, repair children (a
        // once-skipped subagent would otherwise be lost forever), then re-post session-end — all
        // idempotent server-side (deterministic lifecycle ids → no-ops when they already
        // succeeded). If either lifecycle POST fails, return Failed so a re-run retries the repair
        // instead of reporting a falsely-complete Skipped (mirrors Cursor).
        if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) {
            var repairPayload = BuildSessionStartPayload(c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp);

            if (ctx.VisibilityStampFor(c.Status) is { } repairVisibility) {
                repairPayload["default_visibility"] = repairVisibility;
            }

            if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/antigravity",
                    repairPayload, ct))
                return ImportOutcome.Failed;

            // Capture whether the repair actually attached new nested-child content — true
            // iff any child's SendTranscriptBatches sent >0 server-accepted lines. The
            // lifecycle-only repair path (subagent-start+stop, no content resend) contributes
            // false. This outcome stays hardcoded Skipped — only SentChildContent is threaded
            // through below.
            var sentChildContent = await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ctx.Progress, ct);

            // Explicit gen_metadata usage pass — even though AlreadyLoaded sends zero real
            // transcript lines (the content watermark is already at the tip), a session
            // re-imported after this feature shipped may still be missing its USAGE lines
            // (they were never emitted on the original import). Always attempted, always
            // before session-end.
            await PostUsageLinesAsync(ctx, c.SessionId, transcriptPath, c.Meta.FirstTimestamp, ct);

            if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/antigravity",
                    BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), ct))
                return ImportOutcome.Failed;

            return new ImportSessionResult(ImportOutcome.Skipped, sentChildContent);
        }

        // Lifecycle-before-transcript (mirrors Gemini): a transcript that advances the
        // watermark past a failed lifecycle POST would leave the session lifecycle-less.
        // Re-runs are idempotent server-side (deterministic lifecycle ids).
        var startPayload = BuildSessionStartPayload(c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(c.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/antigravity", startPayload, ct))
            return ImportOutcome.Failed;

        var startLine = c.Status == ImportCommand.ClassificationStatus.Partial ? c.ResumeFromLine : 0;

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: c.SessionId,
                filePath: transcriptPath, agentId: null, startLine: startLine, vendor: Vendor, progress: ctx.Progress);
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return ImportOutcome.Failed;
        }

        // Explicit gen_metadata usage pass — decodes the sibling conversation .db and posts the
        // synthetic USAGE lines the live watcher would have streamed, so a content-only import
        // still gains cost. Best-effort (cost is never load-bearing); always before
        // session-end.
        await PostUsageLinesAsync(ctx, c.SessionId, transcriptPath, c.Meta.FirstTimestamp, ct);

        // Children as subagents — BEFORE session-end so SubagentCompleted precedes SessionEnded.
        // Subagent failures don't fail the (already-imported) parent; a re-import retries.
        await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ctx.Progress, ct);

        if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/antigravity",
                BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), ct))
            return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    /// <summary>
    /// Returns <c>true</c> iff any child's <see cref="SessionImporter.SendTranscriptBatches"/>
    /// call sent &gt;0 server-accepted lines during this pass — the <c>SentChildContent</c>
    /// signal threaded onto the <c>AlreadyLoaded</c> caller's <see cref="ImportSessionResult"/>.
    /// The lifecycle-only repair branch below (subagent-start+stop, no content resend)
    /// contributes <c>false</c> — it's a repair, not new content.
    /// </summary>
    async Task<bool> ImportChildrenAsync(
            HttpClient client, string baseUrl, string rootId,
            IReadOnlyDictionary<string, object?> sourceMeta, IProgress<ImportProgress>? progress, CancellationToken ct) {
        if (!sourceMeta.TryGetValue("Children", out var kidsObj) || kidsObj is not List<string> { Count: > 0 } children)
            return false;

        // The root this session was discovered under — children resolve under the SAME root. Falls
        // back to the GUI root for a pre-ProductRoot SourceMeta (there is no such caller in-tree,
        // but the fold stays truthful rather than silently mis-resolving a CLI child).
        var productRoot = sourceMeta.TryGetValue("ProductRoot", out var pr) && pr is string { Length: > 0 } p
            ? p
            : _paths.Root;

        var anyChildContentSent = false;

        foreach (var childId in children) {
            ct.ThrowIfCancellationRequested();

            // childId is the DASHED conversation id — it names the on-disk brain dir, so it
            // resolves the transcript path. The server-facing agent_id is its DASHLESS canonical
            // form: the server canonicalizes agent_id on both ingest and watermark read, so this
            // matches live routing/correlation and the dashless session ids used everywhere else
            // (mirrors GeminiImportSource).
            var childTranscript = _paths.TranscriptFullPathUnder(productRoot, childId);
            if (!File.Exists(childTranscript)) continue;

            var childAgentId = ImportCommand.NormalizeGuid(childId);

            // Where the child's own (rootId, childAgentId) stream is already ingested up to.
            int? chwm;
            try {
                chwm = await FetchServerLastLineAsync(client, baseUrl, rootId, childAgentId, ct);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // watermark probe failed for this child — skip it, retry on re-import
            }

            // Compare the server HWM against the child's last IMPORT-RELEVANT line (same gate the
            // parent uses for AlreadyLoaded): trailing normalizer-skipped steps mustn't force an
            // endless re-send, and a fully-ingested child is a no-op we skip before touching hooks.
            int? childLastImportable;
            try {
                var (lastNonBlank, lastRelevant, _) = await ReadTranscriptStatsAsync(childTranscript, ct);
                childLastImportable = lastRelevant ?? lastNonBlank;
            } catch {
                continue; // unreadable child transcript — retry on re-import
            }
            if (childLastImportable is null) continue;                       // empty child

            if (chwm is { } done && done >= childLastImportable) {
                // Content is fully ingested, but subagent-stop is best-effort below and may have
                // failed AFTER the content POST on a prior run — leaving the subagent with no
                // completion event (HWM only tracks content lines, not lifecycle). Repair it by
                // re-posting subagent-start THEN subagent-stop. The server's stop endpoint no-ops
                // unless the agent is marked active, and that mark is transient in-memory — lost on
                // a server restart between the failed stop and this re-import. Re-posting start
                // restores the active mark (idempotent: deterministic SubagentStarted id, and
                // already-active is a no-op) so the following stop clears it and appends the
                // (idempotent, deterministic) SubagentCompleted. Child content is not re-sent.
                //
                // strict=true on BOTH: the server's start endpoint returns a fail-open 200 even
                // when RecordAgentStartAsync throws and rolls back the active mark, so a non-strict
                // start would be treated as success and the following stop would silently no-op
                // (no active agent → no SubagentCompleted). strict surfaces the failure as non-2xx,
                // so we only post stop when start truly re-marked the agent, and a re-import retries
                // otherwise.
                if (await PostHookAsync(client, baseUrl, "subagent-start",
                        BuildSubagentStartPayload(rootId, childAgentId, childTranscript, strict: true), ct))
                    await PostHookAsync(client, baseUrl, "subagent-stop",
                        BuildSubagentStopPayload(rootId, childAgentId, childTranscript, strict: true), ct);

                continue;
            }

            // Resume by FILE POSITION (startLine), numbering surviving lines by their true position
            // (offset 0) — matching the parent's resume and live capture, so line numbers stay
            // stable across re-imports. (Content-hashed event ids dedupe already-sent lines, but a
            // shifted line number would corrupt $lineNumber + the watermark for genuinely-new lines.)
            var childStartLine = chwm is { } v ? checked(v + 1) : 0;

            // Fail-closed: no content unless the subagent registered first.
            if (!await PostHookAsync(client, baseUrl, "subagent-start",
                    BuildSubagentStartPayload(rootId, childAgentId, childTranscript), ct))
                continue;

            int childSent;
            try {
                // failOnError: true — SentChildContent must reflect only server-ACCEPTED
                // batches; a non-2xx now throws and is caught below, so a rejected batch
                // never flips the signal (Qodo finding 3).
                childSent = await SessionImporter.SendTranscriptBatches(
                    httpClient: client, baseUrl: baseUrl, sessionId: rootId,
                    filePath: childTranscript, agentId: childAgentId, startLine: childStartLine, vendor: Vendor,
                    failOnError: true, progress: progress);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // leave subagent-stop unsent; a re-import retries (idempotent)
            }

            if (childSent > 0) anyChildContentSent = true;

            await PostHookAsync(client, baseUrl, "subagent-stop",
                BuildSubagentStopPayload(rootId, childAgentId, childTranscript), ct);
        }

        return anyChildContentSent;
    }

    // ── payload builders ────────────────────────────────────────────────────────

    static JsonObject BuildSessionStartPayload(string sid, string? cwd, DateTimeOffset? startedAt) {
        var p = new JsonObject { ["hook_event_name"] = "sessionStart", ["session_id"] = sid };
        if (cwd is not null) p["cwd"] = cwd;
        // fail-open git-root discovery, mirroring ImportChainsAsync
        // so routed imports carry the same workspace_root the file-based path does.
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) p["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) p["started_at"] = ts.ToString("O");
        p["origin"] = ImportOrigins.Historical;
        return p;
    }

    static JsonObject BuildSessionEndPayload(string sid, string? cwd, DateTimeOffset? endedAt) {
        var p = new JsonObject { ["hook_event_name"] = "sessionEnd", ["session_id"] = sid, ["reason"] = "antigravity-import" };
        if (cwd is not null) p["cwd"] = cwd;
        if (endedAt is { } ts) p["ended_at"] = ts.ToString("O");
        p["origin"] = ImportOrigins.Historical;
        return p;
    }

    // strict=true makes the server return non-2xx when the lifecycle write itself fails, rather
    // than a fail-open 200 — so a caller that gates on the POST result learns the server actually
    // recorded the event.

    static JsonObject BuildSubagentStartPayload(string parentSid, string agentId, string transcriptPath, bool strict = false) {
        var p = new JsonObject {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSid,
            ["agent_id"]        = agentId,
            ["agent_type"]      = "subagent",
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = "",
        };
        if (strict) p["strict"] = true;
        return p;
    }

    // The stop route binds SubagentStopHook, whose REQUIRED fields include stop_hook_active,
    // agent_transcript_path, and last_assistant_message. Omitting them makes the server reject the
    // body at binding (before HandleSubagentStop runs), so strict is never honored and PostHookAsync
    // returns false — the repair would then loop start→failed-stop forever. Send the full shape,
    // mirroring GeminiSubagentDiscovery.BuildStopPayload.
    static JsonObject BuildSubagentStopPayload(string parentSid, string agentId, string transcriptPath, bool strict = false) {
        var p = new JsonObject {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSid,
            ["agent_id"]               = agentId,
            ["agent_type"]             = "subagent",
            ["transcript_path"]        = transcriptPath,
            ["cwd"]                    = "",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = transcriptPath,
            ["last_assistant_message"] = "",
        };
        if (strict) p["strict"] = true;
        return p;
    }

    // Synthetic USAGE line numbers live in a high band so they never collide with real transcript
    // line numbers (which start at 0). MUST match the live path's WatchCommand.
    // AntigravityUsageLineBase AND the server's SessionWriter.AntigravityUsageLineBase — all
    // three are pinned to the same literal (grepped: WatchCommand.cs:1692,
    // SessionWriter.TranscriptPipeline.cs:21) since the server derives the USAGE event id from
    // line CONTENT (gen_row), not the line number, so the number is only a non-colliding
    // ordering hint.
    const long AntigravityUsageLineBase = 1_000_000_000L;

    /// <summary>
    /// Decodes the sibling conversation <c>.db</c>'s <c>gen_metadata</c> rows into synthetic
    /// USAGE transcript lines and posts them directly via <c>/hooks/transcript</c> — NOT through
    /// <see cref="SessionImporter.SendTranscriptBatches"/>, whose AlreadyLoaded-derived startLine
    /// would skip a USAGE line entirely (it lives past the real-content watermark, in the high
    /// band). Deterministic USAGE event ids (keyed on gen_row) dedupe server-side, so re-posting
    /// on every re-import is safe. Called for every classification (New/Partial AND
    /// AlreadyLoaded) so a session imported before this feature shipped — content but no cost —
    /// gains usage on a bare re-import even though zero physical transcript lines are (re)sent.
    /// Best-effort: cost is never load-bearing — a decode/post failure
    /// here must never fail the import.
    /// </summary>
    static async Task<bool> PostUsageLinesAsync(
            ImportContext ctx, string sessionId, string transcriptPath, DateTimeOffset? createdAt, CancellationToken ct) {
        if (AntigravityPaths.ConversationDbFromTranscript(transcriptPath) is not { } dbPath) return true;

        var rows = AntigravityGenMetadataDb.ReadUsageLines(dbPath, afterIdx: -1, createdAt: createdAt?.ToString("O"));
        if (rows.Count == 0) return true;

        var lines       = new string[rows.Count];
        var lineNumbers = new int[rows.Count];
        for (var i = 0; i < rows.Count; i++) {
            lines[i]       = rows[i].Line;
            lineNumbers[i] = (int)(AntigravityUsageLineBase + rows[i].Idx);
        }

        return await PostTranscriptLinesAsync(ctx.HttpClient, ctx.BaseUrl, sessionId, lines, lineNumbers, ct);
    }

    static async Task<bool> PostTranscriptLinesAsync(
            HttpClient client, string baseUrl, string sessionId, string[] lines, int[] lineNumbers, CancellationToken ct) {
        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            Lines       = lines,
            LineNumbers = lineNumbers,
            Vendor      = "antigravity",
            Strict      = false,
        };
        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

        try {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false; // cost is always best-effort — never let a post failure surface
        }
    }

    static async Task<bool> PostHookAsync(HttpClient client, string baseUrl, string route, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        }
    }

    // ── transcript reading ──────────────────────────────────────────────────────

    static DateTimeOffset? ReadFirstTimestamp(string path) {
        try {
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("created_at", out var ca)
                 && ca.GetString() is { } s
                 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    return ts;
                return null; // first non-blank line had no created_at — don't scan the whole file
            }
        } catch { /* unreadable → null */ }
        return null;
    }

    static async Task<(int? LastNonBlank, int? LastRelevant, int NonBlank)> ReadTranscriptStatsAsync(
            string transcriptPath, CancellationToken ct) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx = null, lastRelevantIdx = null;
        var count = 0; var lineIdx = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;
                if (IsImportRelevantLine(line)) lastRelevantIdx = lineIdx;
            }
            lineIdx++;
        }
        return (lastIdx, lastRelevantIdx, count);
    }

    /// <summary>
    /// True when a line maps to a canonical event under AntigravityTranscriptNormalizer —
    /// a conversational or tool-result step. Noise steps (SYSTEM_*, CHECKPOINT,
    /// CONVERSATION_HISTORY, GENERIC, INVOKE_SUBAGENT) are skipped server-side and can never
    /// advance the watermark; keep in sync with the normalizer.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t)
                && t.GetString() is { } type
                && RelevantTypes.Contains(type);
        } catch {
            return false;
        }
    }

    static DateTimeOffset? TryGetLastWriteUtc(string path) {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    static async Task<int?> FetchServerLastLineAsync(
            HttpClient http, string baseUrl, string sessionId, string? agentId, CancellationToken ct) {
        var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={Uri.EscapeDataString(agentId)}" : "");
        using var resp = await http.GetWithRetryAsync(url, ct: ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32() : null;
    }

    static ImportCommand.SessionClassification Make(
            DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status,
            int totalLines, string? probeError = null) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = HarnessId.Antigravity,
        ProbeErrorReason = probeError,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };
}
