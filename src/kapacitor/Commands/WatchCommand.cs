using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using kapacitor.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace kapacitor.Commands;

static partial class WatchCommand {
    public static async Task<int> RunWatch(
            string  baseUrl,
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string? cwd,
            bool    skipTitle = false
        ) {
        // Redirect all output to a log file so we don't hold parent's pipe FDs open
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "kapacitor", "logs");
        Directory.CreateDirectory(logDir);
        var logKey    = agentId is not null ? $"{sessionId}-{agentId}" : sessionId;
        var logPath   = Path.Combine(logDir, $"{logKey}.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        using var cts = new CancellationTokenSource();

        // Handle SIGTERM/SIGINT for graceful shutdown
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            cts.Cancel();
        };
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

        var state = new WatchState();

        if (skipTitle) {
            state.TitleGenerated = true;
        }

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
            state.LastRepoDetection = DateTimeOffset.UtcNow;
        }

        Log($"Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        // Build SignalR hub connection
        var hubUrl = $"{baseUrl}/hubs/sessions";

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options => {
                    options.AccessTokenProvider = async () => {
                        var t = await TokenStore.GetValidTokensAsync();

                        return t?.AccessToken;
                    };
                }
            )
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain
                        .Insert(0, KapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Register StopWatcher handler — server sends this to tell us to shut down
        hubConnection.On<string>(
            "StopWatcher",
            reason => {
                Log($"Received StopWatcher signal: {reason}");
                cts.Cancel();
            }
        );

        hubConnection.Reconnecting += ex => {
            Log($"SignalR reconnecting: {ex?.Message}");

            return Task.CompletedTask;
        };

        hubConnection.Reconnected += async connectionId => {
            Log($"SignalR reconnected: {connectionId}");

            // Re-register with server and check if it's behind us (gap recovery)
            try {
                var serverPosition = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cancellationToken: cts.Token);

                if (serverPosition < state.LinesProcessed) {
                    Log($"Server behind ({serverPosition} vs {state.LinesProcessed}), rewinding to resend gap");
                    state.LinesProcessed = serverPosition;
                }
            } catch (Exception ex) {
                Log($"Re-register after reconnect failed: {ex.Message}");
            }
        };

        hubConnection.Closed += ex => {
            Log($"SignalR connection closed: {ex?.Message}");
            cts.Cancel();

            return Task.CompletedTask;
        };

        // Connect with retry — server may not be up yet.
        // Retry indefinitely with exponential backoff (cap 30s).
        // The watcher is lightweight when idle and the session will end via session-end hook.
        var connectRetryDelay = TimeSpan.FromSeconds(1);

        while (!cts.Token.IsCancellationRequested) {
            try {
                await hubConnection.StartAsync(cts.Token);

                break;
            } catch (OperationCanceledException) {
                // SIGTERM/SIGINT during connect — exit gracefully
                break;
            } catch (Exception ex) {
                Log($"SignalR connect failed, retrying in {connectRetryDelay.TotalSeconds}s: {ex.Message}");

                try {
                    await Task.Delay(connectRetryDelay, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }

                connectRetryDelay = TimeSpan.FromSeconds(Math.Min(connectRetryDelay.TotalSeconds * 2, 30));
            }
        }

        if (cts.Token.IsCancellationRequested) {
            await hubConnection.DisposeAsync();
            await logWriter.DisposeAsync();

            return 0;
        }

        // Register with server and get resume position
        state.LinesProcessed = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cts.Token);
        Log($"Connected via SignalR, resuming from line {state.LinesProcessed}");

        try {
            while (!cts.Token.IsCancellationRequested) {
                // Skip work while disconnected — SignalR auto-reconnect handles recovery.
                // No point re-reading the file or attempting sends that will fail.
                if (hubConnection.State != HubConnectionState.Connected) {
                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    } catch (OperationCanceledException) {
                        break;
                    }

                    continue;
                }

                // Periodically refresh repository info (every 60s)
                if (cwd is not null && DateTimeOffset.UtcNow - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
                    state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                }

                await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, cts.Token);

                try {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } catch (OperationCanceledException) {
            // Expected
        }

        // Final drain before exit
        if (agentId is null && !state.ThresholdReached) {
            // Session watcher never reached threshold — short-lived session.
            // Skip sending buffered lines so the server can clean up the empty session.
            Log($"Session below threshold ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} lines), skipping final drain");
        } else {
            Log("Draining remaining lines...");
            await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, CancellationToken.None);
        }

        // Signal drain complete to server
        try {
            if (hubConnection.State == HubConnectionState.Connected) {
                await hubConnection.InvokeAsync("WatcherDrainComplete", sessionId, agentId, cancellationToken: cts.Token);
                Log("Drain complete signaled to server");
            }
        } catch (Exception ex) {
            Log($"Failed to signal drain complete: {ex.Message}");
        }

        Log($"Done. {state.LinesProcessed} total lines processed.");

        await hubConnection.DisposeAsync();
        await logWriter.DisposeAsync();

        return 0;
    }

    static readonly Regex CommandNameRegex = CommandNameRx();
    static          bool  parseErrorLogged;

    static async Task DrainNewLines(
            HubConnection     hubConnection,
            string            sessionId,
            string            transcriptPath,
            string?           agentId,
            WatchState        state,
            CancellationToken ct
        ) {
        try {
            if (!File.Exists(transcriptPath)) {
                return;
            }

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();

            await using var stream = new FileStream(
                transcriptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(stream);

            var lineIndex = 0;

            while (await reader.ReadLineAsync(ct) is { } line) {
                if (lineIndex < state.LinesProcessed) {
                    lineIndex++;

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            var linesRead = lineIndex;

            // Capture first user text (needed for title generation)
            if (state is { TitleGenerated: false, FirstUserText: null } && agentId is null) {
                // If we resumed from a later position, scan from the beginning of the file
                if (state is { LinesProcessed: > 0, FullFileScanDone: false }) {
                    Log("Scanning full file for first user text (resumed from later position)");

                    try {
                        await using var scanStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var       scanReader = new StreamReader(scanStream);

                        while (await scanReader.ReadLineAsync(ct) is { } scanLine) {
                            if (string.IsNullOrWhiteSpace(scanLine)) {
                                continue;
                            }

                            var userText = TryExtractUserText(scanLine);

                            if (userText is null) {
                                continue;
                            }

                            SetFirstUserText(state, userText);

                            if (state.FirstUserText is not null) {
                                break;
                            }
                        }

                        state.FullFileScanDone = true;
                    } catch (Exception ex) {
                        Log($"Full file scan for user text failed: {ex.Message}");
                    }
                }

                // Also check new lines (normal path)
                if (state.FirstUserText is null && newLines.Count > 0) {
                    foreach (var userText in newLines.Select(TryExtractUserText).OfType<string>()) {
                        SetFirstUserText(state, userText);

                        if (state.FirstUserText is not null) {
                            break;
                        }
                    }
                }

                if (state.FirstUserText is not null) {
                    Log($"First user text captured ({state.FirstUserText.Length} chars)");
                }

                // Send truncated user text as the initial title immediately
                // (deferred until threshold is reached for session watchers)
                if (state is { InitialTitleSent: false, FirstUserText: not null, ThresholdReached: true } && agentId is null) {
                    state.InitialTitleSent = true;
                    _                      = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                }
            }

            // Count events and capture assistant context for LLM title generation
            if (state is { TitleGenerated: false } && agentId is null && newLines.Count > 0) {
                foreach (var line in newLines) {
                    if (IsEvent(line)) {
                        state.EventCount++;
                    }

                    if (state.FirstAssistantText is null) {
                        var assistantText = TryExtractAssistantText(line);

                        if (assistantText is not null) {
                            state.FirstAssistantText = assistantText.Length > 300 ? assistantText[..300] : assistantText;
                            Log($"First assistant text captured ({state.FirstAssistantText.Length} chars)");
                        }
                    }
                }
            }

            // Generate LLM title after enough events have accumulated
            // (deferred until threshold is reached for session watchers)
            if (state is { TitleGenerated: false, TitleInFlight: false, TitleAttempts: < 5, ThresholdReached: true }
             && agentId is null
             && state.FirstUserText is not null
             && state.EventCount >= 5) {
                Log($"Triggering LLM title generation (attempt {state.TitleAttempts + 1}/5, events: {state.EventCount})");
                state.TitleInFlight = true;
                state.TitleAttempts++;
                _ = GenerateTitleAsync(hubConnection, sessionId, state);
            }

            // ── Buffering for session watchers ──────────────────────────────
            // Hold lines until threshold is reached to avoid polluting the server
            // with short-lived sessions (e.g. <local-command-caveat> prompts).
            // Subagent watchers (agentId != null) skip buffering entirely.
            if (agentId is null && !state.ThresholdReached && newLines.Count > 0) {
                state.BufferedLines.AddRange(newLines);
                state.BufferedLineNumbers.AddRange(newLineNumbers);
                state.LinesReadAhead = linesRead;

                if (state.BufferedLines.Count < WatchState.TranscriptThreshold) {
                    Log($"Buffering {newLines.Count} line(s) ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} threshold)");

                    return;
                }

                // Threshold reached — flush the entire buffer
                Log($"Threshold reached ({state.BufferedLines.Count} lines), flushing buffer");
                state.ThresholdReached = true;
                newLines       = [..state.BufferedLines];
                newLineNumbers = [..state.BufferedLineNumbers];
                linesRead      = state.LinesReadAhead;
                state.BufferedLines.Clear();
                state.BufferedLineNumbers.Clear();

                // Send the initial title now that we're flushing
                if (state is { InitialTitleSent: false, FirstUserText: not null }) {
                    state.InitialTitleSent = true;
                    _ = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                }
            } else if (agentId is null && !state.ThresholdReached) {
                // No new content lines while buffering — track file position
                state.LinesReadAhead = linesRead;

                return;
            }

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            if (newLines.Count == 0 && repoToSend is null) {
                // No content lines and no repo changes — safe to advance past blank/whitespace lines
                state.LinesProcessed = linesRead;

                return;
            }

            // Serialize repository payload to JSON string for the hub method
            var repoJson = repoToSend is not null
                ? JsonSerializer.Serialize(repoToSend, KapacitorJsonContext.Default.RepositoryPayload)
                : null;

            try {
                await hubConnection.InvokeAsync(
                    "SendTranscriptBatch",
                    sessionId,
                    agentId,
                    newLines.ToArray(),
                    newLineNumbers.ToArray(),
                    repoJson,
                    ct
                );

                if (newLines.Count > 0) {
                    Log($"Sent {newLines.Count} line(s) via SignalR");
                }

                if (repoToSend is not null) {
                    Log("Sent updated repository info via SignalR");
                }

                // Only advance position after successful send — if send fails,
                // the next drain cycle will re-read and resend the same lines.
                // KurrentDB event IDs are deterministic (from transcript UUIDs),
                // so re-sending is idempotent.
                state.LinesProcessed = linesRead;

                if (repoToSend is not null) {
                    state.LastSentRepository = repoToSend;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                if (newLines.Count > 0) {
                    Log($"SendTranscriptBatch failed, will retry from line {state.LinesProcessed}: {ex.Message}");
                } else {
                    // Repo-only batch failed — no transcript lines at risk, so advance
                    // position and defer retry to the next 60s repo detection cycle
                    state.LinesProcessed    = linesRead;
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                    Log($"Repo info send failed, will retry in 60s: {ex.Message}");
                }
            }
        } catch (IOException ex) {
            Log($"Error reading file: {ex.Message}");
        } catch (OperationCanceledException) {
            // Expected during shutdown
        }
    }

    static void SetFirstUserText(WatchState state, string userText) {
        var cmdMatch = CommandNameRegex.Match(userText);

        if (cmdMatch.Success) {
            // Skip slash commands — wait for the next real user prompt to generate the title
            Log($"Skipping slash command /{cmdMatch.Groups[1].Value} for title generation");

            return;
        }

        state.FirstUserText = userText;
    }

    static string? TryExtractAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant") {
                return null;
            }

            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) {
                return null;
            }

            if (content.ValueKind != JsonValueKind.Array) {
                return null;
            }

            foreach (var block in content.EnumerateArray()) {
                var blockType = block.TryGetProperty("type", out var btProp) ? btProp.GetString() : null;

                if (blockType == "text" && block.TryGetProperty("text", out var txt)) {
                    var text = txt.GetString()?.Trim();

                    if (!string.IsNullOrEmpty(text)) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static bool IsEvent(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            var       type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            return type is "user" or "assistant";
        } catch {
            return false;
        }
    }

    internal static string? TryExtractUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user") {
                return null;
            }

            // Skip system-injected meta messages (e.g. <local-command-caveat>)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True) {
                return null;
            }

            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) {
                return null;
            }

            switch (content.ValueKind) {
                case JsonValueKind.String: {
                    var text = content.GetString();

                    // Skip local command output — not real user input
                    return text?.StartsWith("<local-command-stdout>") == true ? null : StripSystemInstructions(text);
                }
                // Handle array content (e.g., tool results with [{type:"text", text:"..."}])
                case JsonValueKind.Array: {
                    foreach (var element in content.EnumerateArray()) {
                        if (element.ValueKind                                            == JsonValueKind.Object
                         && element.TryGetProperty("type", out var t)   && t.GetString() == "text"
                         && element.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String) {
                            var text = txt.GetString();

                            if (text?.StartsWith("<local-command-stdout>") == false) {
                                return StripSystemInstructions(text);
                            }
                        }
                    }

                    break;
                }
            }
        } catch (Exception ex) {
            if (!parseErrorLogged) {
                parseErrorLogged = true;
                Log($"TryExtractUserText parse error (further errors suppressed): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Strips XML-like system instruction blocks from user text so they don't pollute title generation.
    /// Removes content between tags like &lt;system_instructions&gt;, &lt;system-reminder&gt;, etc.
    /// </summary>
    internal static string? StripSystemInstructions(string? text) {
        if (text is null) {
            return null;
        }

        var stripped = SystemInstructionsRegex.Replace(text, "").Trim();

        return stripped.Length > 0 ? stripped : null;
    }

    [GeneratedRegex(@"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemInstructionsRx();

    static readonly Regex SystemInstructionsRegex = SystemInstructionsRx();

    static async Task GenerateTitleAsync(HubConnection hubConnection, string sessionId, WatchState state) {
        try {
            var userText      = state.FirstUserText!;
            var truncatedUser = userText.Length > 500 ? userText[..500] : userText;

            var promptBuilder = new System.Text.StringBuilder();
            promptBuilder.Append("Generate a short descriptive title (max 10 words, no quotes, no period) for a coding session. ");
            promptBuilder.Append("NEVER ask a question. NEVER include a question mark. Always produce a title, even if context is limited.");
            promptBuilder.Append($"\n\nThe user's request: {truncatedUser}");

            if (state.FirstAssistantText is not null) {
                promptBuilder.Append($"\n\nThe assistant's initial response: {state.FirstAssistantText}");
            }

            var result = await ClaudeCliRunner.RunAsync(promptBuilder.ToString(), TimeSpan.FromSeconds(15), Log);

            if (result is null) {
                Log($"Title generation attempt {state.TitleAttempts}/5 returned no result (empty response from claude CLI)");
                state.TitleInFlight = false;

                return;
            }

            // Strip markdown formatting (bold, italic, code) from generated title
            var title = StripMarkdown(result.Result);

            // Strip trailing question mark if the model still produced one
            title = title.TrimEnd('?').TrimEnd();

            // Sanity-check: limit to 120 chars
            if (title.Length > 120) {
                title = title[..120];
            }

            Log($"Title usage: model={result.Model} input={result.InputTokens} output={result.OutputTokens} cost=${result.CostUsd:F4}");

            await PostTitleAsync(
                hubConnection,
                sessionId,
                title,
                result.Model,
                result.InputTokens,
                result.OutputTokens,
                result.CacheReadTokens,
                result.CacheWriteTokens,
                state
            );
        } catch (Exception ex) {
            Log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    static async Task SendInitialTitleAsync(HubConnection hubConnection, string sessionId, string title) {
        try {
            await hubConnection.InvokeAsync("SendTitle", sessionId, title, null, 0L, 0L, 0L, 0L);
            Log($"Initial title sent: {title}");
        } catch (Exception ex) {
            Log($"Initial title send failed: {ex.Message}");
        }
    }

    static async Task PostTitleAsync(
            HubConnection hubConnection,
            string        sessionId,
            string        title,
            string?       model,
            long          inputTokens,
            long          outputTokens,
            long          cacheReadTokens,
            long          cacheWriteTokens,
            WatchState    state
        ) {
        try {
            await hubConnection.InvokeAsync("UpdateTitle", sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
            Log($"LLM title generated: {title}");
            state.TitleGenerated = true;
        } catch (Exception ex) {
            Log($"LLM title send failed: {ex.Message}");
        }

        state.TitleInFlight = false;
    }

    internal static bool RepoPayloadChanged(RepositoryPayload? current, RepositoryPayload? lastSent) {
        if (current is null) {
            return false;
        }

        if (lastSent is null) {
            return true;
        }

        return current.Owner  != lastSent.Owner
         || current.RepoName  != lastSent.RepoName
         || current.Branch    != lastSent.Branch
         || current.PrNumber  != lastSent.PrNumber
         || current.PrUrl     != lastSent.PrUrl
         || current.PrTitle   != lastSent.PrTitle
         || current.PrHeadRef != lastSent.PrHeadRef;
    }

    static string TruncateForTitle(string text, int maxLength) {
        // Take first line only, then truncate
        var firstLine  = text.AsSpan();
        var newlineIdx = firstLine.IndexOfAny('\r', '\n');

        if (newlineIdx >= 0) {
            firstLine = firstLine[..newlineIdx];
        }

        if (firstLine.Length <= maxLength) {
            return firstLine.ToString().Trim();
        }

        // Find last word boundary before maxLength
        var truncated = firstLine[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2) {
            truncated = truncated[..lastSpace];
        }

        return $"{truncated.ToString().Trim()}...";
    }

    internal static string StripMarkdown(string text) {
        // Strip bold/italic markers, inline code backticks, and heading prefixes
        text = MarkdownRegex().Replace(text, "");

        return text.Trim();
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [watch] {message}");

    public static int CountFileLines(string path) {
        try {
            if (!File.Exists(path)) {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;

            while (reader.ReadLine() is not null) {
                count++;
            }

            return count;
        } catch {
            return 0;
        }
    }

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Compiled)]
    private static partial Regex CommandNameRx();

    [GeneratedRegex("[*_`#]+")]
    private static partial Regex MarkdownRegex();

    /// <summary>
    /// Retries SignalR reconnection indefinitely with exponential backoff capped at 30s.
    /// The watcher is lightweight when idle — it should never give up while the session is active.
    /// </summary>
    sealed class InfiniteRetryPolicy : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount switch {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
    }
}
