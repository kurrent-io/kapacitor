using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Config;

namespace kapacitor.Commands;

static class HistoryCommand {
    public static async Task<int> HandleHistory(string baseUrl, string? filterCwd, string? filterSession = null, int minLines = 10) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        Console.WriteLine("Discovering sessions...");

        var projectsDir = ClaudePaths.Projects;

        if (!Directory.Exists(projectsDir)) {
            Console.WriteLine("No Claude Code projects directory found.");

            return 0;
        }

        // Discover transcript files: ~/.claude/projects/{encoded-cwd}/{sessionId}.jsonl
        // Skip files inside subagent directories.
        // Deduplicate directories by resolved path — symlinked project dirs (e.g., agent worktrees
        // pointing to the main project dir) would otherwise scan the same files multiple times.
        var transcriptFiles = new List<(string SessionId, string FilePath, string EncodedCwd)>();
        var seenRealPaths   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cwdDir in Directory.GetDirectories(projectsDir)) {
            var realPath = new DirectoryInfo(cwdDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? Path.GetFullPath(cwdDir);

            if (!seenRealPaths.Add(realPath)) continue;

            var encodedCwd = Path.GetFileName(cwdDir);

            transcriptFiles.AddRange(
                from jsonlFile in Directory.GetFiles(cwdDir, "*.jsonl")
                let sessionId = NormalizeGuid(Path.GetFileNameWithoutExtension(jsonlFile))
                select (sessionId, jsonlFile, encodedCwd)
            );
        }

        if (transcriptFiles.Count == 0) {
            Console.WriteLine("No transcript files found.");

            return 0;
        }

        // Filter by session ID if specified
        if (filterSession is not null) {
            filterSession = NormalizeGuid(filterSession);
            transcriptFiles = [.. transcriptFiles.Where(t => t.SessionId == filterSession)];

            if (transcriptFiles.Count == 0) {
                await Console.Error.WriteLineAsync($"Session not found: {filterSession}");

                return 1;
            }
        }

        // Filter by cwd if specified
        if (filterCwd is not null) {
            var normalizedFilter = filterCwd.TrimEnd('/');

            transcriptFiles = [
                .. transcriptFiles
                    .Where(t => {
                            var extractedCwd = ExtractCwdFromTranscript(t.FilePath);

                            return extractedCwd?.TrimEnd('/').Equals(normalizedFilter, StringComparison.Ordinal) == true;
                        }
                    )
            ];
        }

        var projectCount = transcriptFiles.Select(t => t.EncodedCwd).Distinct().Count();
        Console.WriteLine($"Found {transcriptFiles.Count} session{(transcriptFiles.Count == 1 ? "" : "s")} in {projectCount} project{(projectCount == 1 ? "" : "s")}");
        Console.WriteLine();

        // Build continuation map: group sessions by slug and order by timestamp
        // so we can link continuations during migration
        var continuationMap = BuildContinuationMap(transcriptFiles);

        // Sort transcript files so continuation chains are processed in order
        SortByContinuationOrder(transcriptFiles, continuationMap);

        var loaded       = 0;
        var resumed      = 0;
        var skipped      = 0;
        var errored      = 0;
        var excludedRepos = AppConfig.Load()?.ExcludedRepos;

        foreach (var (sessionId, filePath, encodedCwd) in transcriptFiles) {
            // Skip transcripts that are kapacitor-spawned sub-sessions (title generation, what's-done summaries)
            if (IsKapacitorSubSession(filePath)) {
                skipped++;

                continue;
            }

            // Check server status via last-line API
            HistorySessionStatus status;
            int                  resumeFromLine = 0;

            try {
                var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line");

                switch (resp.StatusCode) {
                    case System.Net.HttpStatusCode.NotFound:
                        // 404 = stream doesn't exist, full load needed
                        status = HistorySessionStatus.New; break;
                    case System.Net.HttpStatusCode.NoContent:
                        // 204 = stream exists but no line numbers, skip
                        status = HistorySessionStatus.AlreadyLoaded; break;
                    default: {
                        if (resp.IsSuccessStatusCode) {
                            // 200 = has line numbers, can resume
                            var json = await resp.Content.ReadAsStringAsync();
                            var doc  = JsonDocument.Parse(json);

                            if (doc.RootElement.TryGetProperty("last_line_number", out var prop) && prop.ValueKind == JsonValueKind.Number) {
                                resumeFromLine = prop.GetInt32() + 1;
                                status         = HistorySessionStatus.Partial;
                            } else {
                                status = HistorySessionStatus.AlreadyLoaded;
                            }
                        } else {
                            Console.WriteLine($"Skipping {sessionId} [server error: HTTP {(int)resp.StatusCode}]");
                            errored++;

                            continue;
                        }

                        break;
                    }
                }
            } catch (HttpRequestException ex) {
                Console.WriteLine($"Skipping {sessionId} [server unreachable: {ex.Message}]");
                errored++;

                continue;
            }

            if (status == HistorySessionStatus.AlreadyLoaded) {
                Console.WriteLine($"Skipping {sessionId} [already loaded]");
                skipped++;

                continue;
            }

            // Count total lines for progress display
            var totalLines = WatchCommand.CountFileLines(filePath);

            switch (status) {
                // Skip short transcripts (likely trivial sessions with no meaningful work)
                case HistorySessionStatus.New when minLines > 0 && totalLines < minLines:
                    Console.WriteLine($"Skipping {sessionId} [too short: {totalLines} lines < {minLines} minimum]");
                    skipped++;

                    continue;
                case HistorySessionStatus.New: {
                    // Extract metadata from transcript for session-start hook
                    var meta = ExtractSessionMetadata(filePath);

                    // POST synthesized session-start hook
                    continuationMap.TryGetValue(sessionId, out var prevSessionId);

                    // Note: default_visibility is deliberately omitted for history imports.
                    // Historical sessions predate the user's visibility preference; null falls
                    // back to org_public behavior, which is the safest default for imported data.
                    var startHook = new JsonObject {
                        ["session_id"]      = sessionId,
                        ["transcript_path"] = filePath,
                        ["cwd"]             = meta.Cwd ?? DecodeCwdFromDirName(encodedCwd),
                        ["source"]          = "Startup",
                        ["hook_event_name"] = "session_start",
                        ["model"]           = meta.Model
                    };

                    if (meta.FirstTimestamp is not null) {
                        startHook["started_at"] = meta.FirstTimestamp.Value.ToString("O");
                    }

                    // Pass continuation info directly (bypasses pending continuation mechanism)
                    if (prevSessionId is not null) {
                        startHook["previous_session_id"] = prevSessionId;
                    }

                    if (meta.Slug is not null) {
                        startHook["slug"] = meta.Slug;
                    }

                    // Enrich with repository info if we have a cwd
                    var startCwd = meta.Cwd ?? DecodeCwdFromDirName(encodedCwd);

                    if (startCwd is not null) {
                        var repo = await RepositoryDetection.DetectRepositoryAsync(startCwd);

                        // Check repo exclusion
                        if (excludedRepos is { Length: > 0 }
                         && repo?.Owner is not null
                         && repo.RepoName is not null
                         && excludedRepos.Contains($"{repo.Owner}/{repo.RepoName}", StringComparer.OrdinalIgnoreCase)) {
                            if (Console.IsInputRedirected) {
                                Console.WriteLine($"Skipping {sessionId} [repository {repo.Owner}/{repo.RepoName} is excluded]");
                                skipped++;
                                continue;
                            }

                            Console.Write($"Repository {repo.Owner}/{repo.RepoName} is excluded from tracking. Continue anyway? (y/N) ");
                            var answer = Console.ReadLine()?.Trim();

                            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) {
                                Console.WriteLine($"Skipping {sessionId}");
                                skipped++;
                                continue;
                            }
                        }

                        if (repo is not null) {
                            var repoNode = new JsonObject();
#pragma warning disable IDE0011
                            if (repo.UserName is not null) repoNode["user_name"]   = repo.UserName;
                            if (repo.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;
                            if (repo.RemoteUrl is not null) repoNode["remote_url"] = repo.RemoteUrl;
                            if (repo.Owner is not null) repoNode["owner"]          = repo.Owner;
                            if (repo.RepoName is not null) repoNode["repo_name"]   = repo.RepoName;
                            if (repo.Branch is not null) repoNode["branch"]        = repo.Branch;
#pragma warning restore IDE0011
                            startHook["repository"] = repoNode;
                        }
                    }

                    try {
                        using var startContent = new StringContent(startHook.ToJsonString(), Encoding.UTF8, "application/json");

                        var startResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-start", startContent);

                        if (!startResp.IsSuccessStatusCode) {
                            Console.WriteLine($"Skipping {sessionId} [session-start failed: HTTP {(int)startResp.StatusCode}]");
                            errored++;

                            continue;
                        }
                    } catch (HttpRequestException ex) {
                        Console.WriteLine($"Skipping {sessionId} [server unreachable: {ex.Message}]");
                        errored++;

                        continue;
                    }

                    Console.Write($"Loading {sessionId}... ");

                    // Import transcript with interleaved agent lifecycle events
                    var importResult = await SessionImporter.ImportSessionAsync(
                        httpClient, baseUrl, filePath, sessionId, meta, prevSessionId, encodedCwd
                    );

                    Console.WriteLine($"{importResult.LinesSent} lines [new]");

                    if (importResult.AgentIds.Count > 0) {
                        Console.WriteLine($"  {importResult.AgentIds.Count} agent{(importResult.AgentIds.Count == 1 ? "" : "s")} imported inline");
                    }

                    // POST synthesized session-end hook
                    var lastTimestamp = ExtractLastTimestamp(filePath);

                    var endHook = new JsonObject {
                        ["session_id"]      = sessionId,
                        ["transcript_path"] = filePath,
                        ["cwd"]             = startCwd ?? "",
                        ["reason"]          = "Other",
                        ["hook_event_name"] = "session_end"
                    };

                    if (lastTimestamp is not null) {
                        endHook["ended_at"] = lastTimestamp.Value.ToString("O");
                    }

                    try {
                        using var endContent = new StringContent(endHook.ToJsonString(), Encoding.UTF8, "application/json");
                        await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-end", endContent);
                    } catch {
                        // Best effort for session end
                    }

                    loaded++;

                    break;
                }
                default: {
                    // Partial load — resume from where we left off
                    Console.Write($"Loading {sessionId}... ");
                    var linesSent = await SessionImporter.SendTranscriptBatches(httpClient, baseUrl, sessionId, filePath, agentId: null, startLine: resumeFromLine);
                    Console.WriteLine($"{linesSent} lines [resuming from line {resumeFromLine}]");
                    resumed++;

                    break;
                }
            }
        }

        Console.WriteLine();
        Console.Write($"Done: {loaded} loaded, {resumed} resumed, {skipped} skipped");
        if (errored > 0) Console.Write($", {errored} errored");
        Console.WriteLine();

        return 0;
    }

    internal static SessionMetadata ExtractSessionMetadata(string filePath) {
        var meta = new SessionMetadata();

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 50) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Skip file-history-snapshot entries
                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "file-history-snapshot") {
                        continue;
                    }

                    // Extract cwd from metadata
                    if (meta.Cwd is null && root.TryGetProperty("cwd", out var cwdProp)) {
                        meta.Cwd = cwdProp.GetString();
                    }

                    // Extract model from assistant message
                    if (meta.Model is null                              &&
                        root.TryGetProperty("message", out var msgProp) &&
                        msgProp.TryGetProperty("model", out var modelProp)) {
                        meta.Model = modelProp.GetString();
                    }

                    // Extract slug from metadata
                    if (meta.Slug is null && root.TryGetProperty("slug", out var slugProp) &&
                        slugProp.ValueKind == JsonValueKind.String) {
                        meta.Slug = slugProp.GetString();
                    }

                    // Extract sessionId
                    if (meta.SessionId is null && root.TryGetProperty("sessionId", out var sidProp)) {
                        meta.SessionId = sidProp.GetString();
                    }

                    // Extract first timestamp for continuation ordering
                    if (meta.FirstTimestamp is null              && root.TryGetProperty("timestamp", out var tsProp) &&
                        tsProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(tsProp.GetString(), out var ts)) {
                        meta.FirstTimestamp = ts;
                    }

                    // Stop early once we have all metadata
                    if (meta.Cwd is not null && meta.Model is not null && meta.Slug is not null) {
                        break;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort metadata extraction
        }

        return meta;
    }

    internal static DateTimeOffset? ExtractLastTimestamp(string filePath) {
        try {
            // Read backward from end of file to find the last timestamp without loading everything into memory.
            // Strategy: read the last ~64KB chunk which covers well over 50 JSONL lines.
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int chunkSize = 64 * 1024;
            var       offset    = Math.Max(0, fs.Length - chunkSize);
            fs.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);

            // If we seeked mid-file, skip the first partial line
            if (offset > 0) reader.ReadLine();

            // Collect the last 50 non-empty lines
            var tail = new List<string>(50);

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    tail.Add(line);

                    if (tail.Count > 50) tail.RemoveAt(0);
                }
            }

            // Scan from the end
            for (var i = tail.Count - 1; i >= 0; i--) {
                try {
                    using var doc  = JsonDocument.Parse(tail[i]);
                    var       root = doc.RootElement;

                    if (root.TryGetProperty("timestamp", out var tsProp) &&
                        tsProp.ValueKind == JsonValueKind.String          &&
                        DateTimeOffset.TryParse(tsProp.GetString(), out var ts)) {
                        return ts;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    static string? ExtractCwdFromTranscript(string filePath) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 20) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc = JsonDocument.Parse(line);

                    if (doc.RootElement.TryGetProperty("cwd", out var cwdProp)) {
                        return cwdProp.GetString();
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    static string? DecodeCwdFromDirName(string encodedCwd) => SessionImporter.DecodeCwdFromDirName(encodedCwd);

    /// <summary>
    /// Groups sessions by slug, sorts by timestamp within each group, and builds
    /// a map of sessionId → previousSessionId for sessions that are continuations.
    /// </summary>
    static Dictionary<string, string> BuildContinuationMap(List<(string SessionId, string FilePath, string EncodedCwd)> transcriptFiles) {
        var continuationMap = new Dictionary<string, string>();

        // Extract slug and timestamp from each session
        var metaBySession = new Dictionary<string, (string? Slug, DateTimeOffset Timestamp)>();

        foreach (var (sessionId, filePath, _) in transcriptFiles) {
            var meta = ExtractSessionMetadata(filePath);

            // Use file modification time as fallback when transcript has no timestamp
            var timestamp = meta.FirstTimestamp ?? new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
            metaBySession[sessionId] = (meta.Slug, timestamp);
        }

        // Group by slug and sort by timestamp within each group
        var bySlug = metaBySession
            .Where(kv => kv.Value.Slug is not null)
            .GroupBy(kv => kv.Value.Slug!)
            .ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Value.Timestamp).Select(kv => kv.Key).ToList());

        foreach (var (_, chain) in bySlug) {
            for (var i = 1; i < chain.Count; i++) {
                continuationMap[chain[i]] = chain[i - 1];
            }
        }

        return continuationMap;
    }

    /// <summary>
    /// Sorts transcript files so that within each continuation chain,
    /// earlier sessions are processed before their continuations.
    /// </summary>
    static void SortByContinuationOrder(
            List<(string SessionId, string FilePath, string EncodedCwd)> transcriptFiles,
            Dictionary<string, string>                                   continuationMap
        ) {
        // Build a depth map: sessions with no predecessor = depth 0, their continuations = depth 1, etc.
        var depth = new Dictionary<string, int>();

        foreach (var (sessionId, _, _) in transcriptFiles) {
            GetDepth(sessionId);
        }

        transcriptFiles.Sort((a, b) => {
                var da = depth.GetValueOrDefault(a.SessionId);
                var db = depth.GetValueOrDefault(b.SessionId);

                return da != db ? da.CompareTo(db) : string.CompareOrdinal(a.SessionId, b.SessionId);
            }
        );

        return;

        int GetDepth(string sessionId) {
            if (depth.TryGetValue(sessionId, out var d)) {
                return d;
            }

            d = continuationMap.TryGetValue(sessionId, out var prev) ? GetDepth(prev) + 1 : 0;

            depth[sessionId] = d;

            return d;
        }
    }

    /// <summary>
    /// Detects transcripts created by kapacitor's own headless <c>claude -p</c> invocations
    /// (title generation, what's-done summaries). These sessions start with a <c>queue-operation</c>
    /// entry whose content matches known kapacitor prompt prefixes.
    /// </summary>
    static bool IsKapacitorSubSession(string filePath) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 5) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    // Headless claude -p sessions start with queue-operation entries
                    if (root.TryGetProperty("type", out var typeProp)
                     && typeProp.GetString() == "queue-operation"
                     && root.TryGetProperty("operation", out var opProp)
                     && opProp.GetString() == "enqueue"
                     && root.TryGetProperty("content", out var contentProp)
                     && contentProp.ValueKind == JsonValueKind.String) {
                        var content = contentProp.GetString();

                        if (content is not null && IsKnownKapacitorPrompt(content)) {
                            return true;
                        }
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort — if we can't read the file, don't skip it
        }

        return false;
    }

    static bool IsKnownKapacitorPrompt(string content) =>
        content.StartsWith("Generate a short descriptive title", StringComparison.Ordinal)
     || content.StartsWith("Based on the following session transcript, write a concise summary", StringComparison.Ordinal);

    /// <summary>
    /// Normalize a GUID string to dashless format (matching the live CLI's NormalizeGuidField).
    /// Non-GUID strings are returned as-is.
    /// </summary>
    static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;
}
