using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Discover + classify + import historical DeepSeek Harness (dsh) sessions from the
/// kcap Cordis plugin's per-session logs at <c>~/.cache/kcap/dsh/{id}.jsonl</c> (AI-2020).
/// dsh's session module makes persistence a plugin concern — the plugin forwards each
/// <c>SessionEvent</c> to that file, which the live watcher tails too, so live and
/// historical ingest converge on the server's <c>DeepSeekHarnessTranscriptNormalizer</c>.
/// There is no sibling metadata file: cwd / created-at are read from the plugin's
/// <c>{$kcap:"header", ...}</c> line.
/// Completeness is the server transcript watermark (no client ledger), mirroring
/// <see cref="KiroImportSource"/> (NOT the SQLite-backed OpenCode source).
/// </summary>
internal sealed class DshImportSource : IImportSource {
    readonly string _sessionsDir;

    public DshImportSource(string? sessionsDirOverride = null) {
        _sessionsDir = sessionsDirOverride ?? DshPaths.SessionsDir();
    }

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static string NormalizeForComparison(string path) {
        try {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            return path.TrimEnd('/', '\\');
        }
    }

    public string Vendor => "dsh";

    public bool IsAvailable => Directory.Exists(_sessionsDir);

    /// <summary>True — dsh's <c>session/title</c> line is structural (skipped by the
    /// normalizer) and not reliably extractable here, so we let the server's title
    /// pipeline name imported sessions.</summary>
    public bool SupportsTitleGeneration => true;

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();

        if (!Directory.Exists(_sessionsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        // Flat layout: <sessions>/{id}.jsonl — the kcap dsh Cordis plugin writes
        // ~/.cache/kcap/dsh/{id}.jsonl (one file per session), the same pattern OpenCode uses.
        foreach (var jsonl in GuardedDiscovery.EnumerateFiles(_sessionsDir, "*.jsonl", recursive: false)) {
            ct.ThrowIfCancellationRequested();

            // Filename stem is the session id (the plugin names the file fileFor(session.id)).
            // Use it RAW for both transcript and lifecycle: dsh ids can be non-GUID
            // ("session-<uuid>"), which CanonicalSessionId.Normalize leaves dashed — pre-stripping
            // dashes here (as GUID-id vendors do) would split the transcript and lifecycle onto two
            // streams. The server canonicalizes both identically, so raw-everywhere converges.
            var sessionId = Path.GetFileNameWithoutExtension(jsonl);
            if (string.IsNullOrEmpty(sessionId)) continue;

            if (sessionFilter is not null && !string.Equals(sessionId, sessionFilter, StringComparison.Ordinal))
                continue;

            var header = DshSessionHeader.TryRead(jsonl);

            if (normalizedCwd is not null
             && (header?.Cwd is null || !NormalizeForComparison(header.Cwd).Equals(normalizedCwd, PathComparison)))
                continue;

            // Session-start proxy: the header createdAt, else the transcript's fs birth time.
            var firstTimestamp = header?.CreatedAt;
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(jsonl); } catch { /* best effort */ }
            }

            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      sessionId,
                Vendor:         Vendor,
                Cwd:            header?.Cwd,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"]  = jsonl,
                    ["DashedSessionId"] = sessionId,   // same raw id for lifecycle + transcript
                    ["Cwd"]             = header?.Cwd,
                }));
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

            meta.LastTimestamp ??= TryGetLastWriteUtc(transcriptPath);

            var (excludedRepoKey, excludedPathKey) = ResolveExclusions(s.Cwd, ctx);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

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
                FilePath        = "",  // empty ⇒ routed phase (ImportSessionAsync), same as Kiro/Cursor
                EncodedCwd      = "",
                Meta            = meta,
                Status          = status,
                Vendor          = Vendor,
                ResumeFromLine  = resumeFromLn,
                ExcludedRepoKey = excludedRepoKey,
                ExcludedPathKey = excludedPathKey,
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

        var cwd    = classification.SourceMeta!.TryGetValue("Cwd", out var c) ? c as string : null;
        var dashed = classification.SourceMeta!.TryGetValue("DashedSessionId", out var d) ? d as string : null;

        // Lifecycle uses the dashed id (matches the live hook so a re-import of a live
        // session dedupes); the transcript route uses the dashless id (the stream key).
        var lifecycleId = dashed ?? classification.SessionId;

        var startPayload = BuildSessionStartPayload(lifecycleId, cwd, classification.Meta.FirstTimestamp);
        if (!ctx.ForcePrivate && classification.Status == ImportCommand.ClassificationStatus.New && ctx.DefaultVisibility is not null) {
            startPayload["default_visibility"] = ctx.DefaultVisibility;
        }

        var startOk = await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/dsh", startPayload, ct);
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
                vendor:     Vendor);
        } catch {
            return ImportOutcome.Failed;
        }

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/dsh",
            BuildSessionEndPayload(lifecycleId, cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static JsonObject BuildSessionStartPayload(string sessionId, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
        };
        if (cwd is not null) payload["cwd"] = cwd;
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "historical-import",
        };
        if (cwd is not null) payload["cwd"] = cwd;
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
        Vendor           = "dsh",
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

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
    /// True when the line maps to a canonical event under the server's
    /// DeepSeekHarnessTranscriptNormalizer — <c>user/message</c> / <c>assistant/message</c>
    /// / <c>tool/result</c>. Other types are skipped server-side and never advance the
    /// transcript watermark, so a fully-imported session stays AlreadyLoaded.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Str("type") is "user/message" or "assistant/message" or "tool/result";
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

    static (string? ExcludedRepoKey, string? ExcludedPathKey) ResolveExclusions(string? cwd, ClassifyContext ctx) {
        string? excludedPathKey = null;
        if (cwd is not null && ctx.ExcludedPaths is { Count: > 0 } paths) {
            foreach (var entry in paths) {
                if (PathExclusion.IsExcluded(cwd, [entry])) {
                    excludedPathKey = PathExclusion.Normalize(entry);
                    break;
                }
            }
        }
        return (null, excludedPathKey);
    }
}

/// <summary>
/// Minimal reader for dsh's <c>{type:"session"}</c> header line (the first line of a
/// <c>session.jsonl</c>): the few fields import needs (cwd, created-at). A parse
/// failure must never break discovery (returns null / partial data).
/// </summary>
internal sealed record DshSessionHeader(string? Cwd, DateTimeOffset? CreatedAt) {
    public static DshSessionHeader? TryRead(string transcriptPath) {
        try {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            // The header is the first non-blank line; scan a few lines defensively.
            for (var i = 0; i < 8; i++) {
                var line = reader.ReadLine();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                // The plugin writes {$kcap:"header", ...session.header}; session.header may or may
                // not carry type:"session" (the offline PoC omits it). Accept either marker.
                var isHeader = root.Str("$kcap") == "header" || root.Str("type") == "session";
                if (!isHeader) continue;

                DateTimeOffset? createdAt = null;
                if (root.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number)
                    createdAt = DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64());

                return new DshSessionHeader(root.Str("cwd"), createdAt);
            }
        } catch {
            // fall through
        }
        return null;
    }
}
