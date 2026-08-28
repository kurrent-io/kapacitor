using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Harness.Kiro;

/// <summary>
/// Discover + classify + import historical AWS Kiro CLI sessions from the
/// append-only JSONL logs under <c>~/.kiro/sessions/cli/{id}.jsonl</c> (each the
/// same lines the live watcher tails, so live and historical ingest converge on
/// the server's <c>KiroTranscriptNormalizer</c>). The sibling <c>{id}.json</c>
/// carries cwd / model / title / timestamps. Every JSONL line maps to a canonical
/// event (<c>Prompt</c> / <c>AssistantMessage</c> / <c>ToolResults</c>), so the
/// "last import-relevant line" is simply the last non-blank line.
/// </summary>
internal sealed class KiroImportSource : IImportSource {
    readonly string                                 _sessionsDir;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;

    public KiroImportSource(
        ConfigRoot                              config,
        string                                  sessionsDir,
        Func<string, Task<RepositoryPayload?>>? repoDetector        = null
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

    public string Vendor => "kiro";

    public bool IsAvailable => Directory.Exists(_sessionsDir);

    /// <summary>
    /// False — Kiro names each session in the sibling <c>{id}.json</c>, which
    /// ImportSessionAsync forwards via <c>/hooks/set-title</c>, so the LLM title
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

        if (!Directory.Exists(_sessionsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        foreach (var jsonl in GuardedDiscovery.EnumerateFiles(_sessionsDir, "*.jsonl", recursive: false)) {
            ct.ThrowIfCancellationRequested();

            // Filename stem is the dashed session UUID Kiro uses for both files.
            var dashed = Path.GetFileNameWithoutExtension(jsonl);

            // Kiro session files are UUID-named, and the live hook path rejects
            // non-GUID ids. Apply the same guard so a stray *.jsonl (backup,
            // export, debug dump, partial rename) isn't discovered as a session
            // and then probed against /api/sessions/{id}/... with a non-session id.
            if (!Guid.TryParse(dashed, out _)) continue;

            var dashless = dashed.Replace("-", "");

            if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                continue;

            // The metadata sibling sits next to the .jsonl (respects a custom
            // sessions dir), so derive it from the discovered path rather than
            // the global ~/.kiro location.
            var meta = KiroSessionMeta.TryRead(Path.ChangeExtension(jsonl, ".json"));

            if (normalizedCwd is not null
             && (meta?.Cwd is null || !NormalizeForComparison(meta.Cwd).Equals(normalizedCwd, PathComparison)))
                continue;

            // Session-start proxy: the .json created_at, else the transcript's
            // filesystem birth time (Linux ext4 reports mtime — best effort).
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
                    ["TranscriptPath"]    = jsonl,
                    ["DashedSessionId"]   = dashed,
                    ["Cwd"]               = meta?.Cwd,
                    ["Title"]             = meta?.Title,
                    ["Model"]             = meta?.Model,
                    ["LastTimestamp"]     = meta?.UpdatedAt,
                }));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
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
                LastTimestamp  = s.SourceMeta!.TryGetValue("LastTimestamp", out var lt) ? lt as DateTimeOffset? : null,
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
                // Empty FilePath keeps Kiro on the routed phase (ImportSessionAsync
                // below) — same contract as Cursor/Copilot.
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

        var cwd    = classification.SourceMeta!.TryGetValue("Cwd", out var c) ? c as string : null;
        var dashed = classification.SourceMeta!.TryGetValue("DashedSessionId", out var d) ? d as string : null;
        var model  = classification.SourceMeta!.TryGetValue("Model", out var m) ? m as string : null;

        // Lifecycle uses the dashed id (matches the live agentSpawn hook so a
        // re-import of a live session dedupes); the transcript route uses the
        // dashless id (the canonical stream key). Lifecycle-before-transcript:
        // a transcript that advances the watermark past a failed lifecycle POST
        // would leave the session permanently lifecycle-less.
        var lifecycleId = dashed ?? classification.SessionId;

        var startPayload = BuildSessionStartPayload(lifecycleId, cwd, model, classification.Meta.FirstTimestamp);
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/kiro",
            startPayload,
            ct);
        if (!startOk) return ImportOutcome.Failed;

        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        // Enrich the turn-final assistant lines with per-turn usage (credits /
        // context%) read from the sibling {id}.json — Kiro's JSONL carries none.
        // When there's usage, send an enriched copy (same lines/order/numbers, a
        // _kcap_usage field added to the anchor lines); otherwise send the file
        // as-is. The enriched copy is a temp file SendTranscriptBatches reads.
        var anchors  = KiroUsage.AnchorMap(SafeReadText(Path.ChangeExtension(transcriptPath, ".json")));
        var sendPath = transcriptPath;
        string? enrichedTemp = null;

        if (anchors.Count > 0) {
            enrichedTemp = Path.Combine(Path.GetTempPath(), $"kcap-kiro-usage-{classification.SessionId}-{Guid.NewGuid():N}.jsonl");
            var enriched = (await File.ReadAllLinesAsync(transcriptPath, ct))
                .Select(l => string.IsNullOrWhiteSpace(l) ? l : KiroUsage.EnrichLine(l, anchors));
            await File.WriteAllLinesAsync(enrichedTemp, enriched, ct);
            sendPath = enrichedTemp;
        }

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient,
                baseUrl:    ctx.BaseUrl,
                sessionId:  classification.SessionId,
                filePath:   sendPath,
                agentId:    null,
                startLine:  startLine,
                vendor:     Vendor);
        } catch {
            return ImportOutcome.Failed;
        } finally {
            try { if (enrichedTemp is not null && File.Exists(enrichedTemp)) File.Delete(enrichedTemp); }
            catch { /* best effort */ }
        }

        // Forward Kiro's own session title (best-effort — a title miss must not
        // fail the import).
        if (classification.SourceMeta!.TryGetValue("Title", out var titleObj)
         && titleObj is string title
         && !string.IsNullOrWhiteSpace(title)) {
            await PostSetTitleAsync(ctx.HttpClient, ctx.BaseUrl, classification.SessionId, title, ct);
        }

        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/kiro",
            BuildSessionEndPayload(lifecycleId, cwd, classification.Meta.LastTimestamp),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;

        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    static JsonObject BuildSessionStartPayload(string sessionId, string? cwd, string? model, DateTimeOffset? startedAt) {
        var payload = new JsonObject {
            ["hook_event_name"] = "agentSpawn",
            ["session_id"]      = sessionId,
        };
        if (cwd is not null) payload["cwd"] = cwd;
        // fail-open git-root discovery, mirroring ImportChainsAsync
        // so routed imports carry the same workspace_root the file-based path does.
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) payload["workspace_root"] = workspaceRoot;
        if (model is not null) payload["model"] = model;
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

    static string SafeReadText(string path) {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
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
        Vendor           = "kiro",
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
    /// KiroTranscriptNormalizer — kind <c>Prompt</c> / <c>AssistantMessage</c> /
    /// <c>ToolResults</c>. Other kinds are skipped server-side and never advance
    /// the transcript watermark, so a fully-imported session stays AlreadyLoaded.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Str("kind") is "Prompt" or "AssistantMessage" or "ToolResults";
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
/// Minimal reader for Kiro's per-session <c>{id}.json</c> metadata sibling — the
/// few fields import needs (cwd, title, model, timestamps). A parse failure must
/// never break discovery (returns null / partial data).
/// </summary>
internal sealed record KiroSessionMeta(string? Cwd, string? Title, string? Model, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt) {
    public static KiroSessionMeta? TryRead(string jsonPath) {
        try {
            if (!File.Exists(jsonPath)) return null;
            if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject root) return null;

            return new KiroSessionMeta(
                Cwd:       root["cwd"]?.GetValue<string>(),
                Title:     root["title"]?.GetValue<string>(),
                Model:     root["session_state"]?["rts_model_state"]?["model_info"]?["model_id"]?.GetValue<string>(),
                CreatedAt: ParseTimestamp(root["created_at"]?.GetValue<string>()),
                UpdatedAt: ParseTimestamp(root["updated_at"]?.GetValue<string>()));
        } catch {
            return null;
        }
    }

    static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;
}
