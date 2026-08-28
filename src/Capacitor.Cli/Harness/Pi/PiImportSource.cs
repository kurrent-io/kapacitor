using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Harness.Pi;

/// <summary>
/// Discover + classify + import historical Pi (badlogic/pi-mono) sessions from
/// <c>~/.pi/agent/sessions/**/*.jsonl</c>. Each file is one session in Pi's
/// native tree-structured envelope — the same raw lines the live watcher
/// streams — so historical and live import converge on the server's
/// <c>PiTranscriptNormalizer</c> (vendor <c>pi</c>).
///
/// <para>Pi has no shell hooks, but the server's lifecycle routes
/// (<c>/hooks/session-{start,end}/pi</c>) are still posted here — `SessionStarted`
/// for the capacitor block (visibility) and `SessionEnded` for the summary +
/// fallback title. The session id + cwd + start time come from the JSONL
/// <c>session</c> header line; the end time is the transcript file's last-write
/// time (Pi writes no end marker). Re-runs are idempotent server-side via
/// deterministic lifecycle event ids.</para>
/// </summary>
internal sealed class PiImportSource : IImportSource {
    readonly string                                 _sessionsDir;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;

    public PiImportSource(
        ConfigRoot                              config,
        string                                  sessionsDir,
        Func<string, Task<RepositoryPayload?>>? repoDetector = null
    ) {
        _sessionsDir  = sessionsDir;
        _repoDetector = repoDetector ?? (cwd => RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false));
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

    public string Vendor => "pi";

    public bool IsAvailable => Directory.Exists(_sessionsDir);

    /// <summary>
    /// False — Pi is a routed import source (classifications set
    /// <c>FilePath = ""</c>, so they run through <c>ImportSessionAsync</c>, not
    /// the chain worker). Only the chain worker's <c>OnTitleTaskReady</c> queues
    /// <c>GenerateTitleForImportAsync</c>, and that path is Claude-shaped anyway
    /// (it can't read Pi's <c>type:"message"</c> + <c>message.role</c> lines), so
    /// a routed Pi import never reaches LLM titling. Matching Copilot/Cursor, the
    /// title for an imported Pi session is the server-side fallback stamped by
    /// <c>PiHookHandlers</c> on the synthesized session-end; LIVE Pi sessions
    /// still get an LLM title via the watcher's Pi-aware title extractors.
    /// </summary>
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => false; // no child import

    public async Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        if (!Directory.Exists(_sessionsDir)) return [];

        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var jsonl in GuardedDiscovery.EnumerateFiles(_sessionsDir, "*.jsonl")) {
            ct.ThrowIfCancellationRequested();

            var header = await TryReadHeaderAsync(jsonl, ct);
            if (header is null) continue; // not a Pi session file (no {"type":"session"} header)

            // Validate + normalize the session id the SAME way the live hook path
            // does (PiHookCommand.ExtractSessionId): a valid header uuid wins, else
            // the uuid suffix of "<timestamp>_<uuid>.jsonl". A corrupt/malformed
            // header must NOT mint an arbitrary non-GUID session id and import it.
            if (PiHookCommand.ExtractSessionId(jsonl, header.SessionUuid) is not { } dashless) continue;

            if (!seen.Add(dashless)) continue;
            if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal)) continue;

            if (normalizedCwd is not null
             && (header.Cwd is null || !NormalizeForComparison(header.Cwd).Equals(normalizedCwd, PathComparison))) {
                continue;
            }

            // Session-start proxy: the header timestamp; fall back to the file's
            // birth time (Linux ext4 reports mtime — same degradation as Copilot).
            var firstTimestamp = header.Timestamp;
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(jsonl); } catch { /* best effort */ }
            }

            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      dashless,
                Vendor:         Vendor,
                Cwd:            header.Cwd,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"] = jsonl,
                    ["Cwd"]            = header.Cwd,
                }));
        }

        return result;
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var results     = new List<ImportCommand.SessionClassification>(sessions.Count);
        var repoCache   = new Dictionary<string, string?>(StringComparer.Ordinal);
        var hasExcludes = ctx.ExcludedRepos is { Count: > 0 };

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
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
                continue;
            }

            if (lastNonBlankIndex is null) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript"));
                continue;
            }

            if (nonBlankCount < ctx.MinLines) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlankCount));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlankCount, "watermark probe failed"));
                continue;
            }

            // Pi transcript records carry a per-record "timestamp" field;
            // prefer the tail-scanned last one over file mtime, which can be skewed
            // by unrelated later writes to the same session-scoped file.
            meta.LastTimestamp = EndedAtResolvers.LastTimestampFromJsonl(transcriptPath) ?? TryGetLastWriteUtc(transcriptPath);

            string? repoKey = null;
            if (hasExcludes && s.Cwd is { } cwd) {
                if (!repoCache.TryGetValue(cwd, out repoKey)) {
                    try {
                        var repo = await _repoDetector(cwd);
                        repoKey = repo is { Owner: { } o, RepoName: { } n } ? $"{o}/{n}" : null;
                    } catch {
                        repoKey = null;
                    }
                    repoCache[cwd] = repoKey;
                }
            }

            var (excludedRepoKey, excludedPathKey) = ResolveExclusions(s.Cwd, repoKey, ctx);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            // Compare the watermark against the last IMPORT-RELEVANT line, not
            // the last raw line: skipped entries (model_change / compaction /
            // label / session_info / thinking_level_change) emit nothing and so
            // never advance the server's canonical $lineNumber watermark. Pi
            // sessions routinely END on such lines, so comparing the raw tail
            // would re-classify a complete session as Partial forever (the bug
            // CopilotImportSource.IsImportRelevantLine documents).
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
                FilePath        = "", // routed phase (ImportSessionAsync), like Cursor/Copilot
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

        var cwd = classification.SourceMeta!.TryGetValue("Cwd", out var cwdObj) ? cwdObj as string : null;

        // Lifecycle-before-transcript ordering: a transcript that advanced the
        // watermark past a failed lifecycle POST would leave the session
        // permanently lifecycle-less. Idempotent server-side (deterministic ids).
        var startPayload = BuildSessionStartPayload(classification.SessionId, cwd, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/pi",
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
                vendor:     Vendor);
        } catch {
            return ImportOutcome.Failed;
        }

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/pi",
            BuildSessionEndPayload(classification.SessionId, cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    // ── payloads ──────────────────────────────────────────────────────────

    static JsonObject BuildSessionStartPayload(string sessionId, string? cwd, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = "startup",
        };
        if (cwd is not null) payload["cwd"] = cwd;
        // fail-open git-root discovery, mirroring ImportChainsAsync
        // so routed imports carry the same workspace_root the file-based path does.
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) payload["started_at"] = ts.ToString("O");
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(string sessionId, string? cwd, DateTimeOffset? endedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "pi-import",
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

    // ── header / stats helpers ──────────────────────────────────────────────

    internal sealed record PiHeader(string? SessionUuid, string? Cwd, DateTimeOffset? Timestamp);

    /// <summary>
    /// Reads the first non-blank line and parses it as Pi's <c>session</c>
    /// header. Returns null when the file isn't a Pi session (no leading
    /// <c>{"type":"session",...}</c>) so discovery skips foreign <c>*.jsonl</c>.
    /// </summary>
    static async Task<PiHeader?> TryReadHeaderAsync(string path, CancellationToken ct) {
        try {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;
                if (root.Str("type") != "session") return null;

                DateTimeOffset? ts = root.Str("timestamp") is { } raw
                 && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : null;

                return new PiHeader(root.Str("id"), root.Str("cwd"), ts);
            }
        } catch {
            // Unreadable / malformed → not importable.
        }
        return null;
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
    /// server's <c>PiTranscriptNormalizer</c>: the <c>session</c> header always
    /// emits; <c>compaction</c> emits a <c>ContextCompacted</c>;
    /// <c>branch_summary</c> emits an <c>AssistantTextGenerated</c> with Pi metadata;
    /// <c>message</c> emits for roles user/assistant (non-empty content),
    /// toolResult (has toolCallId), bashExecution (has command). Everything else
    /// (model_change / thinking_level_change / label / session_info / custom*) is
    /// skipped server-side and can never advance the watermark. Keep in sync with
    /// the normalizer.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "session":        return true;
                case "compaction":     return true;
                case "branch_summary": return !string.IsNullOrWhiteSpace(root.Str("summary"));
                case "message":
                    if (root.Obj("message") is not { } msg) return false;
                    return msg.Str("role") switch {
                        "user"          => PiContent.HasUserContent(msg),
                        "assistant"     => PiContent.HasAssistantContent(msg),
                        "toolResult"    => msg.Str("toolCallId") is { Length: > 0 },
                        "bashExecution" => msg.Str("command") is { Length: > 0 },
                        _               => false
                    };
                default: return false;
            }
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
        Vendor           = "pi",
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    static (string? ExcludedRepoKey, string? ExcludedPathKey) ResolveExclusions(
        string? cwd, string? repoKey, ClassifyContext ctx
    ) {
        string? excludedRepoKey = null;
        if (repoKey is not null && ctx.ExcludedRepos is { Count: > 0 } repos
         && repos.Any(r => string.Equals(r, repoKey, StringComparison.OrdinalIgnoreCase))) {
            excludedRepoKey = repoKey;
        }

        string? excludedPathKey = null;
        if (cwd is not null && ctx.ExcludedPaths is { Count: > 0 } paths) {
            foreach (var entry in paths) {
                if (PathExclusion.IsExcluded(cwd, [entry], ctx.Home)) {
                    excludedPathKey = PathExclusion.Normalize(entry, ctx.Home);
                    break;
                }
            }
        }
        return (excludedRepoKey, excludedPathKey);
    }
}
