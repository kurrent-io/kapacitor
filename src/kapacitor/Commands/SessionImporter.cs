using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

/// <summary>
/// Encapsulates the core import logic for a single session transcript, with
/// interleaved agent lifecycle events at the correct chronological position.
/// </summary>
static class SessionImporter {
    /// <summary>
    /// Import a single session: send transcript batches with agent lifecycle
    /// events interleaved at the position where each agent first appears in
    /// <c>progress</c> / <c>agent_progress</c> entries.
    /// </summary>
    internal static async Task<ImportResult> ImportSessionAsync(
            HttpClient      httpClient,
            string          baseUrl,
            string          transcriptPath,
            string          sessionId,
            SessionMetadata metadata,
            string?         previousSessionId,
            string?         encodedCwd
        ) {
        if (!File.Exists(transcriptPath))
            return new ImportResult(sessionId, [], 0);

        var cwd = metadata.Cwd ?? (encodedCwd is not null ? DecodeCwdFromDirName(encodedCwd) : null) ?? "";

        // Discover all agent transcripts on disk
        var agentTranscripts = DiscoverAgentTranscripts(transcriptPath);
        var agentMap         = new Dictionary<string, string>(StringComparer.Ordinal); // agentId → path

        foreach (var (agentId, agentPath) in agentTranscripts) {
            agentMap[agentId] = agentPath;
        }

        // Scan the main transcript to find the first line where each agentId appears
        // in a progress/agent_progress event. This tells us where to interleave.
        var agentFirstLine = ScanAgentProgressLines(transcriptPath);

        // Track which agents were sent inline
        var sentAgents = new HashSet<string>(StringComparer.Ordinal);
        var agentIds   = new List<string>();
        var totalSent  = 0;

        // Read the main transcript line by line, batching and flushing as needed,
        // with agent lifecycle events inserted at the right positions.
        var       batchLines       = new List<string>();
        var       batchLineNumbers = new List<int>();
        const int batchSize        = 100;

        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        var lineIndex = 0;

        while (await reader.ReadLineAsync() is { } line) {
            // Before adding this line to the batch, check if any agent should be
            // interleaved at this position (i.e., the agent's first progress line).
            foreach (var (agentId, firstLine) in agentFirstLine) {
                if (firstLine == lineIndex && !sentAgents.Contains(agentId) && agentMap.TryGetValue(agentId, out var agentPath)) {
                    // Flush the current batch before inserting agent lifecycle
                    if (batchLines.Count > 0) {
                        await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
                        totalSent += batchLines.Count;
                        batchLines.Clear();
                        batchLineNumbers.Clear();
                    }

                    // Send agent lifecycle: start → transcript → stop
                    await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentPath, cwd, transcriptPath);
                    sentAgents.Add(agentId);
                    agentIds.Add(agentId);
                }
            }

            if (!string.IsNullOrWhiteSpace(line)) {
                batchLines.Add(line);
                batchLineNumbers.Add(lineIndex);
            }

            lineIndex++;

            if (batchLines.Count >= batchSize) {
                await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
                totalSent += batchLines.Count;
                batchLines.Clear();
                batchLineNumbers.Clear();
            }
        }

        // Flush remaining main transcript lines
        if (batchLines.Count > 0) {
            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
            totalSent += batchLines.Count;
        }

        // Send any agents that had transcript files but NO progress marker in the
        // main session (e.g., compact agents like acompact-*) as a fallback at the end.
        foreach (var (agentId, agentPath) in agentTranscripts) {
            if (!sentAgents.Contains(agentId)) {
                await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentPath, cwd, transcriptPath);
                sentAgents.Add(agentId);
                agentIds.Add(agentId);
            }
        }

        return new ImportResult(sessionId, agentIds, totalSent);
    }

    /// <summary>
    /// Scan the main transcript and return a map of agentId → first line index
    /// where the agent is referenced. Checks both <c>agent_progress</c> events
    /// and <c>result</c> events with <c>async_launched</c> status.
    /// </summary>
    internal static Dictionary<string, int> ScanAgentProgressLines(string transcriptPath) {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        // Two-pass scan: first collect tool_use_id → line position from assistant
        // messages invoking Agent/Task, then resolve agentId from async_launched results.
        var toolUsePositions = new Dictionary<string, int>(StringComparer.Ordinal); // tool_use_id → line index

        try {
            using var fs     = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var lineIndex = 0;

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    TryExtractAgentReference(line, lineIndex, result, toolUsePositions);
                }

                lineIndex++;
            }
        } catch {
            // Best effort — if we can't scan, agents will be sent at the end
        }

        return result;
    }

    /// <summary>
    /// Parse a single JSONL line and record agent references from:
    /// 1. <c>progress</c> events with <c>data.type == "agent_progress"</c>
    /// 2. <c>assistant</c> messages with Agent/Task <c>tool_use</c> blocks (records tool_use_id → position)
    /// 3. <c>result</c> events with <c>tool_result.status == "async_launched"</c> (resolves agentId via tool_use position)
    /// </summary>
    static void TryExtractAgentReference(
            string                  line,
            int                     lineIndex,
            Dictionary<string, int> result,
            Dictionary<string, int> toolUsePositions
        ) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                return;

            var type = typeProp.GetString();

            switch (type) {
                case "progress":
                    TryExtractFromAgentProgress(root, lineIndex, result);

                    break;
                case "assistant":
                    TryExtractAgentToolUsePositions(root, lineIndex, toolUsePositions);

                    break;
                case "result":
                    TryExtractFromAsyncLaunched(root, lineIndex, result, toolUsePositions);

                    break;
            }
        } catch (JsonException) {
            // Skip malformed lines
        }
    }

    /// <summary>
    /// Extract agentId from <c>progress</c> events with <c>data.type == "agent_progress"</c>.
    /// </summary>
    static void TryExtractFromAgentProgress(JsonElement root, int lineIndex, Dictionary<string, int> result) {
        if (!root.TryGetProperty("data", out var dataProp))
            return;

        if (!dataProp.TryGetProperty("type", out var dataTypeProp) || dataTypeProp.GetString() != "agent_progress")
            return;

        if (!dataProp.TryGetProperty("agentId", out var agentIdProp) || agentIdProp.ValueKind != JsonValueKind.String)
            return;

        var agentId = agentIdProp.GetString();

        if (agentId is not null && !result.ContainsKey(agentId))
            result[agentId] = lineIndex;
    }

    /// <summary>
    /// Extract tool_use positions from <c>assistant</c> messages that invoke Agent/Task tools.
    /// Records tool_use_id → line index for later resolution by async_launched results.
    /// </summary>
    static void TryExtractAgentToolUsePositions(JsonElement root, int lineIndex, Dictionary<string, int> toolUsePositions) {
        // assistant events: root.message.content[] or root.content[]
        var content = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var mc)
            ? mc
            : root.TryGetProperty("content", out var rc)
                ? rc
                : default;

        if (content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray()) {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use"
             && block.TryGetProperty("name", out var bn) && bn.GetString() is "Agent" or "Task"
             && block.TryGetProperty("id", out var bid)   && bid.ValueKind == JsonValueKind.String) {
                var toolUseId = bid.GetString();

                if (toolUseId is not null)
                    toolUsePositions.TryAdd(toolUseId, lineIndex);
            }
        }
    }

    /// <summary>
    /// Extract agentId from <c>result</c> events with <c>tool_result.status == "async_launched"</c>.
    /// Uses the tool_use position (from the assistant message) as the interleave point if available,
    /// otherwise falls back to the result's own line position.
    /// </summary>
    static void TryExtractFromAsyncLaunched(
            JsonElement             root,
            int                     lineIndex,
            Dictionary<string, int> result,
            Dictionary<string, int> toolUsePositions
        ) {
        if (!root.TryGetProperty("tool_result", out var tr) || tr.ValueKind != JsonValueKind.Object)
            return;

        if (!tr.TryGetProperty("status", out var status) || status.GetString() != "async_launched")
            return;

        // Extract agentId (supports both camelCase and snake_case)
        string? agentId = null;

        if (tr.TryGetProperty("agentId", out var aid) && aid.ValueKind == JsonValueKind.String)
            agentId = aid.GetString();
        else if (tr.TryGetProperty("agent_id", out var aid2) && aid2.ValueKind == JsonValueKind.String)
            agentId = aid2.GetString();

        if (agentId is null || result.ContainsKey(agentId))
            return;

        // Prefer the tool_use position (where the agent was invoked) over the result position
        var position = lineIndex;

        if (root.TryGetProperty("tool_use_id", out var tuid) && tuid.ValueKind == JsonValueKind.String) {
            var toolUseId = tuid.GetString();

            if (toolUseId is not null && toolUsePositions.TryGetValue(toolUseId, out var toolUsePos))
                position = toolUsePos;
        }

        result[agentId] = position;
    }

    /// <summary>
    /// Send the full agent lifecycle for one agent: subagent-start → transcript → subagent-stop.
    /// </summary>
    static async Task SendAgentLifecycle(
            HttpClient httpClient,
            string     baseUrl,
            string     sessionId,
            string     agentId,
            string     agentPath,
            string     cwd,
            string     sessionTranscriptPath
        ) {
        // Start agent
        var agentStartHook = new JsonObject {
            ["session_id"]      = sessionId,
            ["transcript_path"] = sessionTranscriptPath,
            ["cwd"]             = cwd,
            ["hook_event_name"] = "subagent_start",
            ["agent_id"]        = agentId,
            ["agent_type"]      = "task"
        };

        try {
            using var agentStartContent = new StringContent(agentStartHook.ToJsonString(), Encoding.UTF8, "application/json");
            await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/subagent-start", agentStartContent);
        } catch {
            // Best effort
        }

        // Send agent transcript
        await SendTranscriptBatches(httpClient, baseUrl, sessionId, agentPath, agentId, startLine: 0);

        // Stop agent
        var agentStopHook = new JsonObject {
            ["session_id"]             = sessionId,
            ["transcript_path"]        = sessionTranscriptPath,
            ["cwd"]                    = cwd,
            ["hook_event_name"]        = "subagent_stop",
            ["agent_id"]               = agentId,
            ["agent_type"]             = "task",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = agentPath,
            ["last_assistant_message"] = ""
        };

        try {
            using var agentStopContent = new StringContent(agentStopHook.ToJsonString(), Encoding.UTF8, "application/json");
            await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", agentStopContent);
        } catch {
            // Best effort
        }
    }

    /// <summary>
    /// Send transcript lines in batches of 100 for a given file (main or agent).
    /// </summary>
    internal static async Task<int> SendTranscriptBatches(
            HttpClient httpClient,
            string     baseUrl,
            string     sessionId,
            string     filePath,
            string?    agentId,
            int        startLine
        ) {
        if (!File.Exists(filePath)) return 0;

        var       totalSent        = 0;
        var       batchLines       = new List<string>();
        var       batchLineNumbers = new List<int>();
        const int batchSize        = 100;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        var lineIndex = 0;

        while (await reader.ReadLineAsync() is { } line) {
            if (lineIndex < startLine) {
                lineIndex++;

                continue;
            }

            if (!string.IsNullOrWhiteSpace(line)) {
                batchLines.Add(line);
                batchLineNumbers.Add(lineIndex);
            }

            lineIndex++;

            if (batchLines.Count >= batchSize) {
                await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers);
                totalSent += batchLines.Count;
                batchLines.Clear();
                batchLineNumbers.Clear();
            }
        }

        // Send remaining lines
        if (batchLines.Count > 0) {
            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers);
            totalSent += batchLines.Count;
        }

        return totalSent;
    }

    static async Task PostTranscriptBatch(
            HttpClient   httpClient,
            string       baseUrl,
            string       sessionId,
            string?      agentId,
            List<string> lines,
            List<int>    lineNumbers
        ) {
        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [.. lines],
            LineNumbers = [.. lineNumbers]
        };

        var       json    = JsonSerializer.Serialize(batch, KapacitorJsonContext.Default.TranscriptBatch);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try {
            await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);
        } catch (HttpRequestException) {
            // Log but continue — don't abort the whole history load for one failed batch
        }
    }

    /// <summary>
    /// Discover agent transcript files in the subagents/ directory alongside the session transcript.
    /// </summary>
    internal static List<(string AgentId, string Path)> DiscoverAgentTranscripts(string sessionTranscriptPath) {
        var results      = new List<(string, string)>();
        var sessionDir   = System.IO.Path.ChangeExtension(sessionTranscriptPath, null);
        var subagentsDir = System.IO.Path.Combine(sessionDir, "subagents");

        if (!Directory.Exists(subagentsDir)) {
            return results;
        }

        results.AddRange(
            from agentFile in Directory.GetFiles(subagentsDir, "agent-*.jsonl")
            let fileName = System.IO.Path.GetFileNameWithoutExtension(agentFile)
            where fileName.StartsWith("agent-")
            let agentId = fileName["agent-".Length..]
            select (agentId, agentFile)
        );

        return results;
    }

    internal static string? DecodeCwdFromDirName(string encodedCwd) {
        // Encoded cwd has / replaced with - (e.g., -Users-alexey-dev-myproject)
        // Reverse: replace leading - with /, then interior - with /
        return string.IsNullOrEmpty(encodedCwd) ? null : encodedCwd.Replace('-', '/');
    }
}

public record ImportResult(string SessionId, List<string> AgentIds, int LinesSent);
