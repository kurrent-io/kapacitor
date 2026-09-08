using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Harness.Gemini;

/// <summary>
/// Discover + classify + import historical Google Gemini CLI sessions from
/// <c>~/.gemini/tmp/&lt;project&gt;/chats/session-&lt;ISO&gt;-&lt;shortId&gt;.jsonl</c>.
/// Each file is one session in Gemini's native chat-recording format — the
/// same lines the live watcher streams — so historical and live import
/// converge on the server's <c>GeminiTranscriptNormalizer</c>.
///
/// <para>The full (dashed) session id lives in the file's header record
/// (<c>sessionId</c>), not the filename (which carries only the 8-char shortId).
/// Gemini does not record the workspace path in a machine-readable header, so
/// import leaves <c>cwd</c> null (no repo enrichment / exclusion on historical
/// import — a v1 limitation; live capture gets cwd from the hook payload). The
/// <c>--cwd</c> filter is honoured best-effort against the project-dir basename.</para>
/// </summary>
internal sealed class GeminiImportSource : IImportSource {
    readonly string _tmpDir;

    public GeminiImportSource(string tmpDir) => _tmpDir = tmpDir;

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public HarnessId Vendor => HarnessId.Gemini;

    public bool IsAvailable => Directory.Exists(_tmpDir);

    /// <summary>
    /// False — Gemini doesn't name its sessions, so there's no CLI-side title
    /// to forward; the server computes a fallback title from the first user
    /// message at session-end (same as the Copilot routed path).
    /// </summary>
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => true; // ImportSubagentsAsync resends on every replay

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var cwdBasename   = filters.FilterCwd is { } fc ? Path.GetFileName(fc.TrimEnd('/', '\\')) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(_tmpDir)) {
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
        }

        foreach (var projectDir in Directory.EnumerateDirectories(_tmpDir)) {
            ct.ThrowIfCancellationRequested();

            try {
                if (cwdBasename is not null
                 && !string.Equals(Path.GetFileName(projectDir), cwdBasename, PathComparison)) {
                    continue;
                }

                var chatsDir = GeminiPaths.ChatsDir(projectDir);
                if (!Directory.Exists(chatsDir)) continue;

                foreach (var file in GuardedDiscovery.EnumerateFiles(chatsDir, "session-*.jsonl", recursive: false)) {
                    var (sessionId, startTime) = ReadHeader(file);

                    if (sessionId is null || !Guid.TryParse(sessionId, out _)) continue;

                    var dashless = sessionId.Replace("-", "");

                    if (!seen.Add(dashless)) continue;
                    if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                        continue;

                    var firstTimestamp = startTime;
                    if (firstTimestamp is null) {
                        try { firstTimestamp = File.GetCreationTimeUtc(file); } catch { /* best effort */ }
                    }

                    if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

                    result.Add(new DiscoveredSession(
                        SessionId:      dashless,
                        Vendor:         Vendor,
                        Cwd:            null,
                        FirstTimestamp: firstTimestamp,
                        SourceMeta:     new Dictionary<string, object?> {
                            ["TranscriptPath"] = file,
                        }));
                }
            } catch {
                // A hostile/inaccessible project subtree must not abort the whole scan.
                continue;
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
            };

            int? lastNonBlankIndex;
            int? lastRelevantIndex;
            int  nonBlankCount;
            try {
                (lastNonBlankIndex, lastRelevantIndex, nonBlankCount) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "transcript read failed"));
                continue;
            }

            if (lastNonBlankIndex is null) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "empty transcript"));
                continue;
            }

            if (nonBlankCount < ctx.MinLines) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.TooShort, totalLines: nonBlankCount));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: nonBlankCount,
                                               probeErrorReason: "watermark probe failed"));
                continue;
            }

            // Gemini chat records carry a per-message "timestamp" field; prefer the
            // tail-scanned last one over file mtime, which can be skewed by unrelated
            // later writes to the same session-scoped file.
            meta.LastTimestamp = EndedAtResolvers.LastTimestampFromJsonl(transcriptPath) ?? TryGetLastWriteUtc(transcriptPath);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            // Compare the server watermark against the last IMPORT-RELEVANT line,
            // not the last raw line: Gemini transcripts end with normalizer-skipped
            // records ($set mutation ops, lastUpdated bumps), so a raw-tail compare
            // would re-classify a complete session Partial forever.
            var lastImportable = lastRelevantIndex ?? lastNonBlankIndex.Value;

            if (serverLastLine is { } srv) {
                if (srv >= lastImportable) {
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                } else {
                    status       = ImportCommand.ClassificationStatus.Partial;
                    resumeFromLn = srv + 1;
                }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId       = s.SessionId,
                // Empty FilePath keeps Gemini on the routed phase (ImportSessionAsync)
                // instead of the Claude/Codex chain worker — same contract as Copilot.
                FilePath        = "",
                EncodedCwd      = "",
                Meta            = meta,
                Status          = status,
                Vendor          = Vendor,
                ResumeFromLine  = resumeFromLn,
                TotalLines      = nonBlankCount,
                SourceMeta      = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;

        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        // Lifecycle-before-transcript ordering (see CopilotImportSource): a
        // transcript that advances the watermark past a failed lifecycle POST
        // would leave the session permanently lifecycle-less. Re-runs are
        // idempotent server-side (deterministic lifecycle event ids).
        var startPayload = BuildSessionStartPayload(classification.SessionId, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/gemini",
            startPayload,
            ct);
        if (!startOk) return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   transcriptPath,
                agentId:    null,
                startLine:  startLine,
                vendor:     Vendor,
                progress:   ctx.Progress);
        } catch {
            return ImportOutcome.Failed;
        }

        // Import nested subagents (chats/<parentSessionId>/<subId>.jsonl) under the parent,
        // BEFORE session-end so their SubagentStarted/Completed land in the parent stream
        // ahead of SessionEnded. Subagent failures don't fail the (already-imported) parent.
        //
        // Capture whether any subagent actually got new content POSTed and ACCEPTED — the
        // SentChildContent signal. KNOWN RESIDUAL: ImportSubagentsAsync has no per-child
        // watermark — it always resends the whole subagent transcript from line 0 on every
        // re-run, so for a session that HAS a subagent this reads true on essentially every
        // re-run whether or not anything is actually new. Accepted — still strictly better
        // than defaulting false (which would risk hiding genuinely-new subagent content
        // behind the lifecycle-only-replay label).
        var sentChildContent = await ImportSubagentsAsync(ctx.HttpClient, ctx.BaseUrl, classification.SessionId, transcriptPath, ctx.Progress, ct);

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/gemini",
            BuildSessionEndPayload(classification.SessionId, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return new ImportSessionResult(startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped, sentChildContent);

        return new ImportSessionResult(startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded, sentChildContent);
    }

    static JsonObject BuildSessionStartPayload(string sessionId, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = "startup",
        };
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "gemini-import",
        };
        if (endedAt is { } ts) payload["ended_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static async Task<bool> PostSyntheticHookAsync(
        HttpClient client, string baseUrl, string routeSegment, JsonObject payload, CancellationToken ct
    ) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{routeSegment}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Imports every TRANSITIVE descendant subagent transcript (root's own
    /// chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl, then each discovered subagent's own
    /// nested dir, recursively — depth-capped) as a DIRECT subagent of the top-level root
    /// (Model A's flat stream key can't express deeper nesting, so flattening is what preserves
    /// content that would otherwise be dropped). Each descendant's agent_name/type is resolved
    /// from its OWN immediate parent's transcript — not the root's — since that's where its
    /// invoke_agent call was recorded; a per-parent cache avoids re-scanning the same transcript
    /// for siblings. subagent-start is fail-closed (skip a descendant's content if start fails)
    /// so a subagent stream never exists without the SubagentStarted that lets chat/trace nest
    /// it. Re-runs are idempotent (deterministic server-side ids). Any descendants beyond the
    /// depth cap, or a descendant directory that couldn't be read, are surfaced (never silent)
    /// via a stderr diagnostic.
    ///
    /// Returns <c>true</c> iff any descendant's <see cref="SessionImporter.SendTranscriptBatches"/>
    /// call sent &gt;0 server-accepted lines — the <c>SentChildContent</c> signal, aggregated
    /// across the ENTIRE recursive descendant subtree (direct children and every deeper
    /// grandchild found by the walk above), not just direct children. Unlike Cursor/Antigravity,
    /// there is no per-descendant watermark here: every re-run resends each descendant's whole
    /// transcript from line 0, so this reads <c>true</c> on essentially every re-run of a session
    /// that has any descendant subagent, whether or not anything is actually new (accepted
    /// residual).
    /// </summary>
    async Task<bool> ImportSubagentsAsync(
        HttpClient client, string baseUrl, string parentSessionIdDashless, string transcriptPath,
        IProgress<ImportProgress>? progress, CancellationToken ct
    ) {
        // Gemini has no completeness-fingerprint ledger (unlike OpenCode), so the omitted ids
        // are unused here — the diagnostic below only needs the count (and, when the below-cap
        // counting walk was truncated, whether that count is a lower bound).
        var (descendants, omitted, _, countTruncated, unreadableDirs) =
            GeminiSubagentDiscovery.EnumerateDescendantFiles(transcriptPath);

        if (omitted > 0) {
            var lowerBoundNote = countTruncated ? " (lower bound — counting ceiling hit)" : "";
            Console.Error.WriteLine(
                $"[kcap] gemini: root {parentSessionIdDashless} descendants_omitted={omitted}{lowerBoundNote} " +
                $"(depth cap {GeminiSubagentDiscovery.MaxDescendantDepth} exceeded)");
        }

        if (unreadableDirs > 0) {
            Console.Error.WriteLine(
                $"[kcap] gemini: root {parentSessionIdDashless} unreadable_descendant_dirs={unreadableDirs} " +
                "(a descendant directory existed but could not be read — its subtree may be incomplete)");
        }

        if (descendants.Count == 0) return false;

        var typesByParentTranscript = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var anySubagentContentSent  = false;

        foreach (var d in descendants) {
            ct.ThrowIfCancellationRequested();

            if (!typesByParentTranscript.TryGetValue(d.ImmediateParentTranscriptPath, out var types)) {
                types = GeminiSubagentDiscovery.ResolveAgentTypes(d.ImmediateParentTranscriptPath);
                typesByParentTranscript[d.ImmediateParentTranscriptPath] = types;
            }

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(d.DashedId); // dashless — matches server routing + correlation
            var agentType = types.GetValueOrDefault(d.DashedId) ?? "subagent";    // agent_name from the IMMEDIATE parent's invoke_agent call

            // Fail-closed: don't stream content unless the subagent registered first.
            var startOk = await PostSyntheticHookAsync(
                client, baseUrl, "subagent-start",
                GeminiSubagentDiscovery.BuildStartPayload(parentSessionIdDashless, agentId, agentType, d.File), ct);
            if (!startOk) continue;

            int subSent;
            try {
                // failOnError: true — SentChildContent must reflect only server-ACCEPTED
                // batches; a non-2xx now throws and is caught below, so a rejected batch
                // never flips the signal (Qodo finding 3).
                subSent = await SessionImporter.SendTranscriptBatches(
                    httpClient: client, baseUrl: baseUrl,
                    sessionId:  parentSessionIdDashless, filePath: d.File,
                    agentId:    agentId, startLine: 0, vendor: Vendor, failOnError: true, progress: progress);
            } catch {
                continue; // leave subagent-stop unsent; a re-import retries (idempotent)
            }

            if (subSent > 0) anySubagentContentSent = true;

            await PostSyntheticHookAsync(
                client, baseUrl, "subagent-stop",
                GeminiSubagentDiscovery.BuildStopPayload(parentSessionIdDashless, agentId, agentType, d.File), ct);
        }

        return anySubagentContentSent;
    }

    static DateTimeOffset? TryGetLastWriteUtc(string path) {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    static ImportCommand.SessionClassification MakeClassification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = HarnessId.Gemini,
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    /// <summary>
    /// Reads the session's header record (first non-blank line) for the full
    /// dashed <c>sessionId</c> and <c>startTime</c>. Gemini always writes the
    /// header first; if the first line isn't one, bail rather than scan the
    /// whole file.
    /// </summary>
    static (string? SessionId, DateTimeOffset? StartTime) ReadHeader(string path) {
        try {
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.Str("sessionId") is not { } sid) return (null, null);

                DateTimeOffset? start = null;
                if (root.Str("startTime") is { } s
                 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) {
                    start = ts;
                }

                return (sid, start);
            }
        } catch { /* unreadable / malformed → skip */ }

        return (null, null);
    }

    static async Task<(int? LastNonBlankIndex, int? LastRelevantIndex, int NonBlankCount)> ReadTranscriptStatsAsync(
        string transcriptPath, CancellationToken ct
    ) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx         = null;
        int? lastRelevantIdx = null;
        var  count           = 0;
        var  lineIdx         = 0;

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
    /// True when the line maps to at least one canonical event under the
    /// server's GeminiTranscriptNormalizer: a <c>gemini</c> message (always
    /// emits thinking/text/tool events), or a <c>user</c> message carrying real
    /// text (NOT the <c>&lt;session_context&gt;</c> bootstrap and NOT a
    /// <c>functionResponse</c>-only tool-result echo). Header records and
    /// <c>$set</c> mutation ops are skipped server-side and can never advance
    /// the watermark — keep this in sync with the normalizer.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("$set") is not null) return false;

            var type = root.Str("type");
            if (type == "gemini") return true;
            if (type != "user") return false; // header (no type) or unknown

            if (root.Str("content") is { } direct) {
                return direct.Length > 0 && !direct.StartsWith("<session_context>", StringComparison.Ordinal);
            }

            if (root.Arr("content") is { } parts) {
                foreach (var part in parts.EnumerateArray()) {
                    if (part.Str("text") is { Length: > 0 } txt
                     && !txt.StartsWith("<session_context>", StringComparison.Ordinal)) {
                        return true;
                    }
                }
            }

            return false;
        } catch {
            return false;
        }
    }

    static async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct) {
        using var resp = await http.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", ct: ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");

        var       body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);

        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
    }
}
