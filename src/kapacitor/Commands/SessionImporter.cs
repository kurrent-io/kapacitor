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
    /// where an <c>agent_progress</c> event references that agent.
    /// </summary>
    static Dictionary<string, int> ScanAgentProgressLines(string transcriptPath) {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        try {
            using var fs     = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var lineIndex = 0;

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    TryExtractAgentProgress(line, lineIndex, result);
                }

                lineIndex++;
            }
        } catch {
            // Best effort — if we can't scan, agents will be sent at the end
        }

        return result;
    }

    /// <summary>
    /// Parse a single JSONL line and, if it's a progress event with
    /// <c>data.type == "agent_progress"</c>, record the first occurrence
    /// of each <c>data.agentId</c>.
    /// </summary>
    static void TryExtractAgentProgress(string line, int lineIndex, Dictionary<string, int> result) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            // Must be a "progress" event
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "progress") {
                return;
            }

            if (!root.TryGetProperty("data", out var dataProp)) {
                return;
            }

            // data.type must be "agent_progress"
            if (!dataProp.TryGetProperty("type", out var dataTypeProp) || dataTypeProp.GetString() != "agent_progress") {
                return;
            }

            // data.agentId is the agent identifier
            if (!dataProp.TryGetProperty("agentId", out var agentIdProp) || agentIdProp.ValueKind != JsonValueKind.String) {
                return;
            }

            var agentId = agentIdProp.GetString();

            if (agentId is not null && !result.ContainsKey(agentId)) {
                result[agentId] = lineIndex;
            }
        } catch (JsonException) {
            // Skip malformed lines
        }
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
