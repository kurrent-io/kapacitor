using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Harness.Copilot;

/// <summary>
/// Discover + classify + import historical GitHub Copilot CLI sessions from
/// <c>$COPILOT_HOME/session-state/&lt;dashed-sid&gt;/events.jsonl</c> (and the
/// pre-GA <c>history-session-state/</c> root). Each events.jsonl is one
/// session in Copilot's native envelope format — the same raw lines the live
/// watcher streams, so historical and live import converge on the server's
/// <c>CopilotTranscriptNormalizer</c>.
///
/// <para>
/// Unlike Cursor there is no lossy workspace encoding to reverse:
/// <c>workspace.yaml</c> in each session dir carries <c>cwd</c>,
/// <c>created_at</c>/<c>updated_at</c>, and Copilot's auto-generated session
/// <c>name</c> (forwarded as the session title via <c>/hooks/set-title</c>,
/// which is why <see cref="SupportsTitleGeneration"/> is false). Session dirs
/// WITHOUT an events.jsonl are failed-startup scaffolding and are skipped.
/// </para>
/// </summary>
internal sealed class CopilotImportSource : IImportSource {
    readonly string                                 _sessionStateDir;
    readonly string                                 _legacySessionStateDir;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;

    public CopilotImportSource(
        ConfigRoot                              config,
        CopilotPaths                            paths,
        Func<string, Task<RepositoryPayload?>>? repoDetector = null
    ) : this(config, paths.SessionStateDir, paths.LegacySessionStateDir, repoDetector) { }

    /// <summary>Both discovery roots given outright, for a caller that walks a layout it laid
    /// out itself rather than Copilot's.</summary>
    public CopilotImportSource(
        ConfigRoot                              config,
        string                                  sessionStateDir,
        string                                  legacySessionStateDir,
        Func<string, Task<RepositoryPayload?>>? repoDetector = null
    ) {
        _sessionStateDir       = sessionStateDir;
        _legacySessionStateDir = legacySessionStateDir;
        _repoDetector          = repoDetector ?? (cwd => RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false));
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

    public string Vendor => "copilot";

    public bool IsAvailable => Directory.Exists(_sessionStateDir) || Directory.Exists(_legacySessionStateDir);

    /// <summary>
    /// False — Copilot names every session itself (workspace.yaml <c>name</c>);
    /// ImportSessionAsync forwards it via /hooks/set-title, so the LLM title
    /// pipeline would only burn tokens to overwrite a good title.
    /// </summary>
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => false; // no child import

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var sessionFilter = filters.FilterSession is { } sf ? ImportCommand.NormalizeGuid(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var result = new List<DiscoveredSession>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        // Current root first — when a pre-GA session was migrated on resume it
        // exists in both roots, and session-state/ has the longer transcript.
        foreach (var root in new[] { _sessionStateDir, _legacySessionStateDir }) {
            if (!Directory.Exists(root)) continue;

            foreach (var sessionDir in Directory.EnumerateDirectories(root)) {
                try {
                    var dirName = Path.GetFileName(sessionDir);
                    var jsonl   = CopilotPaths.EventsJsonl(root, dirName);

                    // Dirs without events.jsonl are failed-startup scaffolding
                    // (workspace.yaml + checkpoints only) — nothing to import.
                    if (!File.Exists(jsonl)) continue;

                    var dashless = dirName.Replace("-", "");

                    if (!seen.Add(dashless)) continue;
                    if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                        continue;

                    var meta = CopilotWorkspaceYaml.TryRead(CopilotPaths.WorkspaceYaml(root, dirName));

                    if (normalizedCwd is not null
                     && (meta?.Cwd is null
                      || !NormalizeForComparison(meta.Cwd).Equals(normalizedCwd, PathComparison))) {
                        continue;
                    }

                    // Session-start proxy: workspace.yaml created_at is written by
                    // Copilot at session creation; fall back to the transcript's
                    // filesystem birth time (same degradation notes as Cursor —
                    // Linux ext4 reports mtime).
                    var firstTimestamp = meta?.CreatedAt;
                    if (firstTimestamp is null) {
                        try { firstTimestamp = File.GetCreationTimeUtc(jsonl); } catch { /* best effort */ }
                    }

                    if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

                    result.Add(new DiscoveredSession(
                        SessionId:      dashless,
                        Vendor:         Vendor,
                        Cwd:            meta?.Cwd,
                        FirstTimestamp: firstTimestamp,
                        SourceMeta:     new Dictionary<string, object?> {
                            ["TranscriptPath"] = jsonl,
                            ["Cwd"]            = meta?.Cwd,
                            ["Name"]           = meta?.Name,
                        }));
                } catch {
                    // A hostile/inaccessible session subtree must not abort the whole scan.
                    continue;
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        // Per-cwd repo cache — sessions cluster inside the same workspace, so
        // RepositoryDetection runs once per unique cwd (mirrors Cursor).
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

            // workspace.yaml's updated_at is stamped when Copilot assigns the
            // session name (near session START), not at session end. The
            // truthful end time is the session-final `session.shutdown`
            // record's own timestamp — read directly off the raw
            // transcript, NOT through IsImportRelevantLine, which deliberately
            // skips session.* records for a different purpose (watermark
            // comparison against the server's normalized line count above).
            // Fall back to a tail-scan of any timestamped record, then mtime.
            meta.LastTimestamp = EndedAtResolvers.CopilotShutdownTimestamp(transcriptPath)
                ?? EndedAtResolvers.LastTimestampFromJsonl(transcriptPath)
                ?? TryGetLastWriteUtc(transcriptPath);

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

            // Compare the server watermark against the last IMPORT-RELEVANT
            // line, not the last raw line: the watermark is the max persisted
            // canonical $lineNumber, and every Copilot transcript ENDS with
            // lines the server normalizer intentionally skips
            // (session.shutdown plus the hook.start/hook.end pair the
            // sessionEnd hook itself writes). Compared against the raw tail, a
            // fully-imported session would re-classify Partial on every run,
            // forever re-sending noise lines that can never advance the
            // watermark.
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
                // Empty FilePath keeps Copilot on the routed phase
                // (ImportSessionAsync below) instead of the Claude/Codex chain
                // worker — same contract as CursorImportSource.
                FilePath        = "",
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

        // Lifecycle-before-transcript ordering contract: see
        // CursorImportSource.ImportSessionAsync — a transcript that advances
        // the watermark past a failed lifecycle POST would leave the session
        // permanently lifecycle-less. Re-runs are idempotent server-side
        // (deterministic lifecycle event ids).
        var startPayload = BuildSessionStartPayload(classification.SessionId, cwd, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/copilot",
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

        // Copilot auto-names every session (workspace.yaml `name`) — forward
        // it as the title. Best-effort: a title miss must not fail the import.
        if (classification.SourceMeta!.TryGetValue("Name", out var nameObj)
         && nameObj is string name
         && !string.IsNullOrWhiteSpace(name)) {
            await PostSetTitleAsync(ctx.HttpClient, ctx.BaseUrl, classification.SessionId, name, ct);
        }

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/copilot",
            BuildSessionEndPayload(classification.SessionId, cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

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

    static async Task PostSetTitleAsync(HttpClient client, string baseUrl, string sessionId, string title, CancellationToken ct) {
        if (title.Length > 120) title = title[..120];

        var payload = new JsonObject {
            ["session_id"] = sessionId,
            ["title"]      = title,
        };

        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _       = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, ct: ct);
        } catch {
            // Best effort.
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
        Vendor           = "copilot",
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
    /// True when the line maps to at least one canonical event under the
    /// server's CopilotTranscriptNormalizer — the contract this mirrors:
    /// <c>session.start</c> always emits; <c>user.message</c> emits for
    /// non-empty <c>content</c>; <c>assistant.message</c> emits per non-empty
    /// reasoningText / content / toolRequests entry;
    /// <c>tool.execution_complete</c> emits when <c>toolCallId</c> is present.
    /// Everything else (hook.*, system.*, assistant.turn_*, session.* telemetry,
    /// subagent.*, tool.execution_start) is skipped server-side and can never
    /// advance the transcript watermark. Fail direction on drift: treating a
    /// skipped line as relevant re-classifies a complete session as Partial
    /// (today's bug); treating an emitting line as noise marks AlreadyLoaded
    /// while its events are unsent — so keep this list in sync with the
    /// normalizer when the mapping grows.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("data") is not { } data) return false;

            return root.Str("type") switch {
                "session.start"           => true,
                "user.message"            => data.Str("content") is { Length: > 0 },
                "assistant.message"       => data.Str("content") is { Length: > 0 }
                                          || data.Str("reasoningText") is { Length: > 0 }
                                          || data.Arr("toolRequests") is { } requests && requests.GetArrayLength() > 0,
                "tool.execution_complete" => data.Str("toolCallId") is not null,
                _                         => false
            };
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

/// <summary>
/// Minimal reader for Copilot's per-session <c>workspace.yaml</c>. The file is
/// flat <c>key: value</c> lines (no nesting, no quoting in practice) — a full
/// YAML dependency would be overkill for the four fields we need, and a parse
/// failure must never break discovery (returns null / partial data instead).
/// </summary>
internal sealed record CopilotWorkspaceYaml(string? Cwd, string? Name, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt) {
    public static CopilotWorkspaceYaml? TryRead(string path) {
        try {
            if (!File.Exists(path)) return null;

            string? cwd       = null;
            string? name      = null;
            DateTimeOffset? createdAt = null;
            DateTimeOffset? updatedAt = null;

            foreach (var line in File.ReadLines(path)) {
                var idx = line.IndexOf(": ", StringComparison.Ordinal);
                if (idx <= 0) continue;

                var key   = line[..idx].Trim();
                var value = Unquote(line[(idx + 2)..].Trim());

                if (value.Length == 0) continue;

                switch (key) {
                    case "cwd":        cwd  = value; break;
                    case "name":       name = value; break;
                    case "created_at": createdAt = ParseTimestamp(value); break;
                    case "updated_at": updatedAt = ParseTimestamp(value); break;
                }
            }

            return new CopilotWorkspaceYaml(cwd, name, createdAt, updatedAt);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Strips matching YAML scalar quotes. Copilot quotes values that contain
    /// YAML-special sequences (e.g. <c>name: 'Reply with exactly: ok'</c>) —
    /// without this the literal quotes leak into imported session titles.
    /// Handles the two YAML escape forms we can hit on one line: doubled
    /// single-quotes inside single-quoted scalars, and backslash-escaped
    /// double-quotes inside double-quoted scalars.
    /// </summary>
    internal static string Unquote(string value) {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') {
            return value[1..^1].Replace("''", "'");
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') {
            return value[1..^1].Replace("\\\"", "\"");
        }

        return value;
    }

    static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var ts)
            ? ts
            : null;
}
