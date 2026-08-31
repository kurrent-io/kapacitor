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
                        await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers, vendor);
                        var flushed = batchLines.Count;
                        totalSent += flushed;
                        progress?.Report(new BatchFlushed(AgentId: null, flushed));
                        batchLines.Clear();
                        batchLineNumbers.Clear();
                    }

                    // Send agent lifecycle: start → transcript → stop
                    agentTypes.TryGetValue(agentId, out var agentType);
                    await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath, progress);
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
                await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers, vendor);
                var flushed = batchLines.Count;
                totalSent += flushed;
                progress?.Report(new BatchFlushed(AgentId: null, flushed));
                batchLines.Clear();
                batchLineNumbers.Clear();
            }
        }

        // Flush remaining main transcript lines
        if (batchLines.Count > 0) {
            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers, vendor);
            var flushed = batchLines.Count;
            totalSent += flushed;
            progress?.Report(new BatchFlushed(AgentId: null, flushed));
        }

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
    /// Send transcript lines in batches of 100 for a given file (main or agent).
    /// </summary>
    /// <param name="vendor">
    /// Stamped on the outgoing <see cref="TranscriptBatch"/> so the server picks the matching
    /// normalizer.
    /// </param>
    /// <param name="abortDelivery">
    /// checked immediately BEFORE
    /// every batch POST (including the very first) AND immediately AFTER it. When it returns true,
    /// delivery aborts by throwing <see cref="TranscriptDeliveryAbortedException"/> — a pre-POST trip
    /// skips the pending batch entirely; a post-POST trip has already sent that batch but stops
    /// before any further one. No remaining batches (or, for the Cursor caller, the child
    /// lifecycle/transcript POSTs that follow this call) are ever sent past a trip either way.
    /// Cursor's live rewrite guard can quarantine a session AFTER this method's caller last checked
    /// (its own check only runs once, before the first byte of this multi-batch send), and without a
    /// check re-run inside the loop every remaining 100-line batch would still be posted. The
    /// post-POST check specifically closes the window a pre-POST-only check leaves open when a
    /// marker appears WHILE a batch is in flight and there is no NEXT batch to gate — e.g. a
    /// transcript of &lt;=100 lines (a single, final batch) — where the method would otherwise return
    /// normally with the caller none the wiser. The predicate should be cheap (a marker-file check,
    /// not a re-run of any correlator) — the caller is expected to close over an already-resolved
    /// identity rather than recompute it per batch.
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
                batchLineNumbers.Add(checked(lineIndex + lineNumberOffset));
            }

            lineIndex++;

            if (batchLines.Count >= batchSize) {
                if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();

                await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers, vendor, failOnError);
                var flushed = batchLines.Count;
                totalSent += flushed;
                progress?.Report(new BatchFlushed(agentId, flushed));
                batchLines.Clear();
                batchLineNumbers.Clear();

                // re-check IMMEDIATELY after the POST too, not
                // only before the NEXT one. Before this, a marker written while THIS batch's POST was
                // in flight was only ever caught by the pre-POST check ahead of a batch that might
                // never come (see below) — for a transcript with no further lines to send (this was
                // the LAST batch, e.g. exactly 100/200/... lines), there IS no next predicate
                // invocation at all, and the method returned normally with the caller none the wiser.
                if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();
            }
        }

        if (batchLines.Count > 0) {
            if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();

            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers, vendor, failOnError);
            var flushed = batchLines.Count;
            totalSent += flushed;
            progress?.Report(new BatchFlushed(agentId, flushed));

            // same post-POST re-check for the trailing/only
            // batch (a transcript of <=100 lines never enters the loop branch above at all, so
            // WITHOUT this check here specifically, a marker written while this — the ONLY — POST was
            // in flight was never observed anywhere: SendTranscriptBatches returned normally and the
            // caller (CursorImportSource) proceeded straight into child lifecycle / normal completion.
            if (abortDelivery?.Invoke() == true) throw new TranscriptDeliveryAbortedException();
        }

        return totalSent;
    }

    /// <summary>
    /// thrown by <see cref="SendTranscriptBatches"/> when its
    /// <c>abortDelivery</c> predicate trips mid-delivery (between batches). Distinct from a generic
    /// send failure so a caller that wants to react specially (Cursor's best-effort session-end —
    /// see <see cref="CursorImportSource.ImportSessionAsync"/>) can catch it before a broader
    /// catch-all.
    /// </summary>
    internal sealed class TranscriptDeliveryAbortedException : Exception;

    static async Task PostTranscriptBatch(
            HttpClient   httpClient,
            string       baseUrl,
            string       sessionId,
            string?      agentId,
            List<string> lines,
            List<int>    lineNumbers,
            HarnessId    vendor,
            bool         failOnError = false
        ) {
        var batch = new TranscriptBatch {
            SessionId   = sessionId,
            AgentId     = agentId,
            Lines       = [.. lines],
            LineNumbers = [.. lineNumbers],
            // Claude stays absent on the wire: an older server reads a missing vendor as Claude,
            // and the tag is what selects any other normalizer.
            Vendor = vendor is HarnessId.Claude ? null : vendor.VendorId,
            // Fail-closed callers (failOnError) also want server-side normalization failures to
            // surface as non-2xx (server only does so when Strict), not just transport/HTTP errors.
            Strict = failOnError
        };

        var       json    = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try {
            using var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);
            if (failOnError && !resp.IsSuccessStatusCode)
                throw new HttpRequestException($"transcript batch rejected: HTTP {(int)resp.StatusCode}");
        } catch (HttpRequestException) {
            // Strict callers (OpenCode import) abort the import; default callers
            // (Claude/Codex/Cursor/Copilot/Gemini/Kiro/Pi) log but continue — unchanged.
            if (failOnError) throw;
        }
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
    /// Count the non-blank lines from <paramref name="startLine"/> (inclusive) to
    /// EOF — i.e. the number of lines <see cref="SendTranscriptBatches"/> /
    /// <see cref="ImportSessionAsync"/> will actually POST for the main transcript.
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
                if (lineIndex >= startLine && !string.IsNullOrWhiteSpace(line)) count++;

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
