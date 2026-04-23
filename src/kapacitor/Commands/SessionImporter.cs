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
            string?         encodedCwd
        ) {
        if (!File.Exists(transcriptPath))
            return new(sessionId, [], 0);

        var cwd = metadata.Cwd ?? (encodedCwd is not null ? DecodeCwdFromDirName(encodedCwd) : null) ?? "";

        // Discover all agent transcripts on disk
        var agentTranscripts = DiscoverAgentTranscripts(transcriptPath);
        var agentMap         = new Dictionary<string, string>(StringComparer.Ordinal); // agentId → path

        foreach (var (agentId, agentPath) in agentTranscripts) {
            agentMap[agentId] = agentPath;
        }

        // Scan the main transcript to find the first line where each agentId appears
        // in a progress/agent_progress event (for interleave position) plus the real
        // subagent_type from the parent Task-tool invocation (for canonical fidelity).
        var scan           = ScanAgentLifecycle(transcriptPath);
        var agentFirstLine = scan.FirstLineByAgent;
        var agentTypes     = scan.AgentTypeByAgent;

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
                    agentTypes.TryGetValue(agentId, out var agentType);
                    await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath);
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
                agentTypes.TryGetValue(agentId, out var agentType);
                await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath);
                sentAgents.Add(agentId);
                agentIds.Add(agentId);
            }
        }

        return new ImportResult(sessionId, agentIds, totalSent);
    }

    /// <summary>
    /// Result of a single-pass transcript scan that resolves both the interleave
    /// position and the subagent type for every agent referenced from the parent.
    /// </summary>
    internal sealed record AgentLifecycleScan(
            Dictionary<string, int>     FirstLineByAgent,
            Dictionary<string, string?> AgentTypeByAgent
        );

    /// <summary>
    /// Scan the main transcript once and return, per agent:
    /// 1. the first line index where the agent is referenced (via <c>agent_progress</c>,
    ///    <c>async_launched</c>, or foreground <c>toolUseResult.agentId</c>), used as the
    ///    interleave position;
    /// 2. the real subagent type pulled from the parent Task-tool invocation's
    ///    <c>input.subagent_type</c>, so canonical <c>SubagentStarted.AgentType</c> carries
    ///    "code-reviewer" / "general-purpose" / "Explore" instead of the generic "task".
    /// </summary>
    /// <remarks>
    /// The caller should fall back to <c>null</c> (or let the server default) when an
    /// agent id has no resolved type — this happens for compact agents and any agent
    /// whose invocation we never observe (e.g. transcripts we discover only by file).
    /// </remarks>
    // ReSharper disable once MemberCanBePrivate.Global
    public static Dictionary<string, int> ScanAgentProgressLines(string transcriptPath) =>
        ScanAgentLifecycle(transcriptPath).FirstLineByAgent;

    internal static AgentLifecycleScan ScanAgentLifecycle(string transcriptPath) {
        var firstLine  = new Dictionary<string, int>(StringComparer.Ordinal);
        var agentTypes = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Two-pass resolution in a single read: first collect tool_use_id → line
        // position AND tool_use_id → subagent_type from assistant messages invoking
        // Agent/Task, then resolve agentId from async_launched results and foreground
        // toolUseResult.agentId entries, carrying the subagent_type through.
        var toolUsePositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var toolUseTypes     = new Dictionary<string, string?>(StringComparer.Ordinal);

        try {
            using var fs     = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var lineIndex = 0;

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    TryExtractAgentReference(line, lineIndex, firstLine, toolUsePositions, toolUseTypes, agentTypes);
                }

                lineIndex++;
            }
        } catch {
            // Best effort — if we can't scan, agents will be sent at the end
        }

        return new AgentLifecycleScan(firstLine, agentTypes);
    }

    /// <summary>
    /// Parse a single JSONL line and record agent references from:
    /// 1. <c>progress</c> events with <c>data.type == "agent_progress"</c>
    /// 2. <c>assistant</c> messages with Agent/Task <c>tool_use</c> blocks (records tool_use_id → position)
    /// 3. <c>result</c> events with <c>tool_result.status == "async_launched"</c> (resolves agentId via tool_use position)
    /// 4. <c>user</c> events with <c>toolUseResult.agentId</c> (foreground agent completions)
    /// </summary>
    static void TryExtractAgentReference(
            string                      line,
            int                         lineIndex,
            Dictionary<string, int>     result,
            Dictionary<string, int>     toolUsePositions,
            Dictionary<string, string?> toolUseTypes,
            Dictionary<string, string?> agentTypes
        ) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            var       type = root.Str("type");

            switch (type) {
                case "progress":
                    TryExtractFromAgentProgress(root, lineIndex, result);

                    break;
                case "assistant":
                    TryExtractAgentToolUsePositions(root, lineIndex, toolUsePositions, toolUseTypes);

                    break;
                case "result":
                    TryExtractFromAsyncLaunched(root, lineIndex, result, toolUsePositions, toolUseTypes, agentTypes);

                    break;
                case "user":
                    TryExtractFromToolUseResult(root, lineIndex, result, toolUsePositions, toolUseTypes, agentTypes);

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
        var data = root.Obj("data");

        if (data?.Str("type") != "agent_progress")
            return;

        var agentId = data.Value.Str("agentId");

        if (agentId is not null)
            result.TryAdd(agentId, lineIndex);
    }

    /// <summary>
    /// Extract tool_use positions and the real <c>subagent_type</c> argument from
    /// <c>assistant</c> messages that invoke Agent/Task tools. Records
    /// tool_use_id → line index for later resolution by async_launched results,
    /// and tool_use_id → subagent_type so the resolved agent carries the real
    /// type (e.g. "code-reviewer") instead of the generic "task".
    /// </summary>
    static void TryExtractAgentToolUsePositions(
            JsonElement                 root,
            int                         lineIndex,
            Dictionary<string, int>     toolUsePositions,
            Dictionary<string, string?> toolUseTypes
        ) {
        // assistant events: root.message.content[] or root.content[]
        var content = root.Obj("message")?.Arr("content") ?? root.Arr("content");

        if (content is not { } arr)
            return;

        foreach (var block in arr.EnumerateArray()) {
            if (block.Str("type") != "tool_use"
             || block.Str("name") is not ("Agent" or "Task")
             || block.Str("id") is not { } toolUseId)
                continue;

            toolUsePositions.TryAdd(toolUseId, lineIndex);

            var subagentType = block.Obj("input")?.Str("subagent_type");
            toolUseTypes.TryAdd(toolUseId, subagentType);
        }
    }

    /// <summary>
    /// Extract agentId from <c>result</c> events with <c>tool_result.status == "async_launched"</c>.
    /// Uses the tool_use position (from the assistant message) as the interleave point if available,
    /// otherwise falls back to the result's own line position.
    /// </summary>
    static void TryExtractFromAsyncLaunched(
            JsonElement                 root,
            int                         lineIndex,
            Dictionary<string, int>     result,
            Dictionary<string, int>     toolUsePositions,
            Dictionary<string, string?> toolUseTypes,
            Dictionary<string, string?> agentTypes
        ) {
        var tr = root.Obj("tool_result");

        if (tr?.Str("status") != "async_launched")
            return;

        var agentId = tr.Value.Str("agentId") ?? tr.Value.Str("agent_id");

        if (agentId is null || result.ContainsKey(agentId))
            return;

        // Prefer the tool_use position (where the agent was invoked) over the result position
        var position = lineIndex;

        if (root.Str("tool_use_id") is { } toolUseId) {
            if (toolUsePositions.TryGetValue(toolUseId, out var toolUsePos))
                position = toolUsePos;

            if (toolUseTypes.TryGetValue(toolUseId, out var subagentType))
                agentTypes.TryAdd(agentId, subagentType);
        }

        result[agentId] = position;
    }

    /// <summary>
    /// Extract agentId from <c>user</c> events where <c>toolUseResult.agentId</c> is present
    /// (foreground/synchronous agent completions). Resolves the interleave position via the
    /// tool_use_id from the message content, falling back to the result's own line position.
    /// </summary>
    static void TryExtractFromToolUseResult(
            JsonElement                 root,
            int                         lineIndex,
            Dictionary<string, int>     result,
            Dictionary<string, int>     toolUsePositions,
            Dictionary<string, string?> toolUseTypes,
            Dictionary<string, string?> agentTypes
        ) {
        var tur = root.Obj("toolUseResult");

        var agentId = tur?.Str("agentId") ?? tur?.Str("agent_id");

        if (agentId is null || result.ContainsKey(agentId))
            return;

        // Find tool_use_id from message.content[].tool_use_id to resolve invocation
        // position and propagate the parent invocation's subagent_type.
        var position = lineIndex;

        if (root.Obj("message")?.Arr("content") is { } content) {
            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") != "tool_result"
                 || block.Str("tool_use_id") is not { } toolUseId)
                    continue;

                if (toolUsePositions.TryGetValue(toolUseId, out var toolUsePos)) {
                    position = toolUsePos;
                }

                if (toolUseTypes.TryGetValue(toolUseId, out var subagentType)) {
                    agentTypes.TryAdd(agentId, subagentType);
                }

                break;
            }
        }

        result[agentId] = position;
    }

    /// <summary>
    /// Send the full agent lifecycle for one agent: subagent-start → transcript → subagent-stop.
    /// </summary>
    /// <param name="agentType">
    /// The real subagent type pulled from the parent Task-tool invocation's
    /// <c>input.subagent_type</c> (e.g. "code-reviewer", "general-purpose", "Explore").
    /// Falls back to "task" when unknown — typically compact agents and transcripts
    /// discovered without a parent invocation.
    /// </param>
    static async Task SendAgentLifecycle(
            HttpClient httpClient,
            string     baseUrl,
            string     sessionId,
            string     agentId,
            string?    agentType,
            string     agentPath,
            string     cwd,
            string     sessionTranscriptPath
        ) {
        var resolvedAgentType = agentType ?? "task";

        // Start agent
        var agentStartHook = new JsonObject {
            ["session_id"]      = sessionId,
            ["transcript_path"] = sessionTranscriptPath,
            ["cwd"]             = cwd,
            ["hook_event_name"] = "subagent_start",
            ["agent_id"]        = agentId,
            ["agent_type"]      = resolvedAgentType
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
            ["agent_type"]             = resolvedAgentType,
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
