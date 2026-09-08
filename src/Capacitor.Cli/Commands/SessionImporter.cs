using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Commands;

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
    /// <param name="vendor">
    /// Stamped on every <see cref="TranscriptBatch"/> so the server's <c>INormalizerSelector</c>
    /// picks the matching normalizer. Codex rollouts have no <c>subagents/</c> sibling directory and
    /// no agent-progress markers, so the Claude agent walk is short-circuited for it; its collab
    /// subagents are discovered from the shared sessions tree by parent_thread_id and appended after
    /// the parent transcript.
    /// </param>
    internal static async Task<ImportResult> ImportSessionAsync(
            HttpClient                 httpClient,
            string                     baseUrl,
            string                     transcriptPath,
            string                     sessionId,
            SessionMetadata            metadata,
            string?                    encodedCwd,
            IProgress<ImportProgress>? progress = null,
            HarnessId                  vendor   = HarnessId.Claude
        ) {
        if (!File.Exists(transcriptPath))
            return new(sessionId, [], 0);

        var cwd = metadata.Cwd ?? (encodedCwd is not null ? DecodeCwdFromDirName(encodedCwd) : null) ?? "";

        // Codex rollouts don't ship a subagents/ sibling directory and don't carry
        // agent-progress markers in-band, so the Claude-shaped agent walk is skipped — we
        // stream the rollout straight through the batch loop tagged as Codex, and pick
        // up collab subagent rollouts (parent_thread_id-linked) after the main transcript.
        var isCodex = vendor is HarnessId.Codex;

        var agentTranscripts = isCodex
            ? []
            : DiscoverAgentTranscripts(transcriptPath);
        var agentMap = new Dictionary<string, string>(StringComparer.Ordinal); // agentId → path

        foreach (var (agentId, agentPath) in agentTranscripts) {
            agentMap[agentId] = agentPath;
        }

        // Scan the main transcript to find, per agent, the earliest line where it is
        // referenced — via agent_progress, an async_launched tool_result, or a
        // foreground toolUseResult.agentId (for interleave position) — plus the real
        // subagent_type from the parent Task-tool invocation (for canonical fidelity).
        Dictionary<string, int>     agentFirstLine;
        Dictionary<string, string?> agentTypes;

        if (isCodex) {
            agentFirstLine = new Dictionary<string, int>(StringComparer.Ordinal);
            agentTypes     = new Dictionary<string, string?>(StringComparer.Ordinal);
        } else {
            var scan = ScanAgentLifecycle(transcriptPath);
            agentFirstLine = scan.FirstLineByAgent;
            agentTypes     = scan.AgentTypeByAgent;
        }

        // Track which agents were sent inline
        var sentAgents = new HashSet<string>(StringComparer.Ordinal);
        var agentIds   = new List<string>();
        var totalSent  = 0;

        // Read the main transcript line by line, batching and flushing as needed,
        // with agent lifecycle events inserted at the right positions.
        var batch = new TranscriptBatchBuffer();

        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        var lineIndex = 0;

        while (await reader.ReadLineAsync() is { } line) {
            // Before adding this line to the batch, check if any agent should be
            // interleaved at this position (i.e., the agent's first progress line).
            foreach (var (agentId, firstLine) in agentFirstLine) {
                if (firstLine == lineIndex && !sentAgents.Contains(agentId) && agentMap.TryGetValue(agentId, out var agentPath)) {
                    // Flush the current batch before inserting agent lifecycle
                    if (!batch.IsEmpty) await FlushAsync();

                    // Send agent lifecycle: start → transcript → stop
                    agentTypes.TryGetValue(agentId, out var agentType);
                    await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath, progress);
                    sentAgents.Add(agentId);
                    agentIds.Add(agentId);
                }
            }

            if (!string.IsNullOrWhiteSpace(line)) {
                var bytes = TranscriptBatchBuffer.SizeOf(line);

                if (bytes > TranscriptBatchBuffer.MaxBytes) {
                    progress?.Report(new LineSkipped(sessionId, AgentId: null, lineIndex, bytes));
                } else {
                    if (!batch.Fits(bytes)) await FlushAsync();

                    batch.Add(line, lineIndex, bytes);

                    if (batch.IsFull) await FlushAsync();
                }
            }

            lineIndex++;
        }

        // Flush remaining main transcript lines
        if (!batch.IsEmpty) await FlushAsync();

        // Send any agents that had transcript files but NO progress marker in the
        // main session (e.g., compact agents like acompact-*) as a fallback at the end.
        foreach (var (agentId, agentPath) in agentTranscripts) {
            if (!sentAgents.Contains(agentId)) {
                agentTypes.TryGetValue(agentId, out var agentType);
                await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath, progress);
                sentAgents.Add(agentId);
                agentIds.Add(agentId);
            }
        }

        // Codex collab subagents (0.146+, multi-agent v2) fork into their own rollouts in the
        // shared sessions tree, linked back via session_meta parent_thread_id — there is no
        // subagents/ sibling dir to walk. Import every TRANSITIVE descendant as a DIRECT
        // subagent of this root (the server's AgentSubsession model is flat, mirroring the
        // Gemini import), AFTER the parent transcript so the interleave-position machinery —
        // which Codex rollouts have no markers for — is simply not needed. Fail-closed like
        // the Gemini/OpenCode descendant imports: no content without an ACKNOWLEDGED
        // subagent-start (a subagent stream must never exist without the SubagentStarted that
        // lets chat/trace nest it), strict transcript delivery, and no subagent-stop after a
        // failed tail — a re-import retries (deterministic event ids make that idempotent).
        if (isCodex) {
            foreach (var sub in CodexSubagentDiscovery.EnumerateDescendantRollouts(transcriptPath, sessionId)) {
                var subType    = CodexSubagentDiscovery.AgentTypeFrom(sub.AgentPath, sub.AgentNickname);
                var subAgentId = sub.ChildDashlessId;

                if (!await PostSubagentHookAsync(httpClient, baseUrl, "subagent-start",
                        CodexSubagentDiscovery.BuildStartPayload(sessionId, subAgentId, subType, sub.FilePath))) {
                    continue;
                }

                progress?.Report(new SubagentStarted(subAgentId));

                int subLines;

                try {
                    subLines = await SendTranscriptBatches(
                        httpClient, baseUrl, sessionId, sub.FilePath, subAgentId,
                        startLine: 0, progress: progress, vendor: HarnessId.Codex, failOnError: true);
                } catch (HttpRequestException) {
                    continue; // leave subagent-stop unsent; a re-import retries (idempotent)
                }

                progress?.Report(new SubagentFinished(subAgentId, subLines));

                await PostSubagentHookAsync(httpClient, baseUrl, "subagent-stop",
                    CodexSubagentDiscovery.BuildStopPayload(sessionId, subAgentId, subType, sub.FilePath));

                agentIds.Add(subAgentId);
            }
        }

        return new ImportResult(sessionId, agentIds, totalSent);

        async Task FlushAsync() =>
            totalSent += await FlushBatchAsync(httpClient, baseUrl, sessionId, agentId: null, batch, vendor, failOnError: false, progress);
    }

    /// <summary>POSTs one subagent lifecycle hook; false on any failure so the caller can
    /// fail closed (skip content for an unregistered subagent) instead of streaming anyway.</summary>
    static async Task<bool> PostSubagentHookAsync(HttpClient httpClient, string baseUrl, string route, JsonObject payload) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
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
    /// An agent id may have no resolved type — e.g. compact agents, or transcripts
    /// we discover only by file with no observed parent invocation. In that case
    /// <see cref="SendAgentLifecycle"/> substitutes the literal <c>"task"</c> on the
    /// outgoing hook payload so the server still records a concrete AgentType.
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

        if (agentId is null)
            return;

        var toolUseId = root.Str("tool_use_id");

        // Always try to propagate subagent_type — an earlier agent_progress reference
        // may already have locked in FirstLineByAgent, but this can still be our first
        // chance to learn the real type from the parent Task invocation.
        if (toolUseId is not null && toolUseTypes.TryGetValue(toolUseId, out var subagentType))
            agentTypes.TryAdd(agentId, subagentType);

        if (result.ContainsKey(agentId))
            return;

        // Prefer the tool_use position (where the agent was invoked) over the result position
        var position = toolUseId is not null && toolUsePositions.TryGetValue(toolUseId, out var toolUsePos)
            ? toolUsePos
            : lineIndex;

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

        if (agentId is null)
            return;

        var alreadyPositioned = result.ContainsKey(agentId);

        // Find tool_use_id from message.content[].tool_use_id to resolve invocation
        // position and propagate the parent invocation's subagent_type. Always try
        // to propagate the type — an earlier agent_progress reference may have
        // already locked in FirstLineByAgent, but the parent invocation's type
        // might still be resolvable here.
        var position = lineIndex;

        if (root.Obj("message")?.Arr("content") is { } content) {
            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") != "tool_result"
                 || block.Str("tool_use_id") is not { } toolUseId)
                    continue;

                if (!alreadyPositioned && toolUsePositions.TryGetValue(toolUseId, out var toolUsePos))
                    position = toolUsePos;

                if (toolUseTypes.TryGetValue(toolUseId, out var subagentType))
                    agentTypes.TryAdd(agentId, subagentType);

                break;
            }
        }

        if (alreadyPositioned)
            return;

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
    static async Task<int> SendAgentLifecycle(
            HttpClient                 httpClient,
            string                     baseUrl,
            string                     sessionId,
            string                     agentId,
            string?                    agentType,
            string                     agentPath,
            string                     cwd,
            string                     sessionTranscriptPath,
            IProgress<ImportProgress>? progress
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

        progress?.Report(new SubagentStarted(agentId));
        var agentLines = await SendTranscriptBatches(httpClient, baseUrl, sessionId, agentPath, agentId, startLine: 0, progress: progress);
        progress?.Report(new SubagentFinished(agentId, agentLines));

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

        return agentLines;
    }

    /// <summary>
    /// Send a file's transcript lines (main or agent) in batches; <see cref="TranscriptBatchBuffer"/>
    /// says when one closes. A line over the byte budget cannot be split, so it is reported as a
    /// <see cref="LineSkipped"/> and left out rather than posted in a batch the server would refuse.
    /// </summary>
    /// <param name="vendor">
    /// Stamped on the outgoing <see cref="TranscriptBatch"/> so the server picks the matching
    /// normalizer.
    /// </param>
    /// <param name="failOnError">
    /// A refused or undeliverable batch throws instead of being reported as a <see cref="BatchDropped"/>
    /// and passed over.
    /// </param>
    /// <param name="abortDelivery">
    /// Checked immediately before and after every batch POST, the first and the last included; a trip
    /// throws <see cref="TranscriptDeliveryAbortedException"/> so nothing further is posted. Cursor's
    /// live rewrite guard can quarantine a session while a batch is in flight, and the post-POST check
    /// is what catches a marker written during the only batch of a short transcript. Keep it cheap: a
    /// marker check over an already-resolved identity, not a correlator run per batch.
    /// </param>
    internal static async Task<int> SendTranscriptBatches(
            HttpClient                 httpClient,
            string                     baseUrl,
            string                     sessionId,
            string                     filePath,
            string?                    agentId,
            int                        startLine,
            IProgress<ImportProgress>? progress          = null,
            HarnessId                  vendor            = HarnessId.Claude,
            int                        lineNumberOffset  = 0,
            bool                       failOnError       = false,
            Func<bool>?                abortDelivery     = null
        ) {
        if (!File.Exists(filePath)) return 0;

        var totalSent = 0;
        var batch     = new TranscriptBatchBuffer();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        var lineIndex = 0;

        while (await reader.ReadLineAsync() is { } line) {
            if (lineIndex < startLine) {
                lineIndex++;

                continue;
            }

            if (!string.IsNullOrWhiteSpace(line)) {
                var bytes      = TranscriptBatchBuffer.SizeOf(line);
                var lineNumber = checked(lineIndex + lineNumberOffset);

                if (bytes > TranscriptBatchBuffer.MaxBytes) {
                    progress?.Report(new LineSkipped(sessionId, agentId, lineNumber, bytes));
                } else {
                    if (!batch.Fits(bytes)) await FlushAsync();

                    batch.Add(line, lineNumber, bytes);

                    if (batch.IsFull) await FlushAsync();
                }
            }

            lineIndex++;
        }

        if (!batch.IsEmpty) await FlushAsync();

        return totalSent;

        async Task FlushAsync() {
            if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();

            totalSent += await FlushBatchAsync(httpClient, baseUrl, sessionId, agentId, batch, vendor, failOnError, progress);

            if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();
        }
    }

    /// <summary>
    /// Thrown by <see cref="SendTranscriptBatches"/> when its <c>abortDelivery</c> predicate trips.
    /// Distinct from a generic send failure so Cursor's best-effort session-end (see
    /// <see cref="CursorImportSource.ImportSessionAsync"/>) can catch it ahead of a broader catch-all.
    /// </summary>
    internal sealed class TranscriptDeliveryAbortedException : Exception;

    /// <summary>
    /// POSTs the buffered lines, empties the buffer and returns how many lines it held. A strict caller
    /// gets a refusal or transport failure as an exception naming the line range; a lenient one gets it
    /// reported as a <see cref="BatchDropped"/> and keeps going, with the lines still counted as posted.
    /// </summary>
    static async Task<int> FlushBatchAsync(
            HttpClient                 httpClient,
            string                     baseUrl,
            string                     sessionId,
            string?                    agentId,
            TranscriptBatchBuffer      batch,
            HarnessId                  vendor,
            bool                       failOnError,
            IProgress<ImportProgress>? progress
        ) {
        var payload = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [.. batch.Lines],
            LineNumbers = [.. batch.LineNumbers],
            // Claude stays absent on the wire: an older server reads a missing vendor as Claude,
            // and the tag is what selects any other normalizer.
            Vendor = vendor is HarnessId.Claude ? null : vendor.VendorId,
            // The server reports a normalization failure as non-2xx only for a strict batch.
            Strict = failOnError
        };

        var       json    = JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.TranscriptBatch);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var       first   = batch.FirstLineNumber;
        var       last    = batch.LastLineNumber;
        string?   loss;

        try {
            using var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);

            loss = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}";
        } catch (HttpRequestException ex) when (!failOnError) {
            loss = ex.Message;
        }

        if (loss is not null) {
            if (failOnError) throw new HttpRequestException($"transcript batch lines {first}-{last} rejected: {loss}");

            progress?.Report(new BatchDropped(sessionId, agentId, first, last, loss));
        }

        var flushed = batch.Count;
        progress?.Report(new BatchFlushed(agentId, flushed));
        batch.Clear();

        return flushed;
    }

    /// <summary>
    /// Discover agent transcript files in the subagents/ directory alongside the session transcript.
    /// </summary>
    internal static List<(string AgentId, string Path)> DiscoverAgentTranscripts(string sessionTranscriptPath) {
        var results      = new List<(string, string)>();
        var sessionDir   = Path.ChangeExtension(sessionTranscriptPath, null);
        var subagentsDir = Path.Combine(sessionDir, "subagents");

        if (!Directory.Exists(subagentsDir)) {
            return results;
        }

        results.AddRange(
            from agentFile in Directory.GetFiles(subagentsDir, "agent-*.jsonl")
            let fileName = Path.GetFileNameWithoutExtension(agentFile)
            where fileName.StartsWith("agent-")
            let agentId = fileName["agent-".Length..]
            select (agentId, agentFile)
        );

        return results;
    }

    /// <summary>
    /// Count the lines from <paramref name="startLine"/> (inclusive) to EOF that
    /// <see cref="SendTranscriptBatches"/> / <see cref="ImportSessionAsync"/> will actually
    /// POST for the main transcript: non-blank and within the batch byte budget.
    /// This is the denominator for the per-session import progress bar: the sum of
    /// every <see cref="BatchFlushed"/> with a null <c>AgentId</c> equals this count.
    /// Best-effort — returns 0 on a missing file or any I/O error, which callers
    /// treat as "total unknown" (the bar stays indeterminate).
    /// </summary>
    internal static int CountSendableLines(string filePath, int startLine = 0) {
        if (!File.Exists(filePath)) return 0;

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var count     = 0;
            var lineIndex = 0;

            while (reader.ReadLine() is { } line) {
                if (lineIndex >= startLine
                 && !string.IsNullOrWhiteSpace(line)
                 && TranscriptBatchBuffer.SizeOf(line) <= TranscriptBatchBuffer.MaxBytes) count++;

                lineIndex++;
            }

            return count;
        } catch {
            return 0;
        }
    }

    internal static string? DecodeCwdFromDirName(string encodedCwd) {
        // Encoded cwd has / replaced with - (e.g., -Users-alexey-dev-myproject)
        // Reverse: replace leading - with /, then interior - with /
        return string.IsNullOrEmpty(encodedCwd) ? null : encodedCwd.Replace('-', '/');
    }
}

public record ImportResult(string SessionId, List<string> AgentIds, int LinesSent);
