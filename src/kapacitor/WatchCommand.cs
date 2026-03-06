using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace kapacitor;

static partial class WatchCommand {
    public static async Task<int> RunWatch(string baseUrl, string sessionId, string transcriptPath, string? agentId, string? cwd) {
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

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
            state.LastRepoDetection = DateTimeOffset.UtcNow;
        }

        Log($"Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        // Build SignalR hub connection
        var hubUrl = $"{baseUrl}/hubs/sessions";
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .AddJsonProtocol(options => {
                options.PayloadSerializerOptions.TypeInfoResolverChain
                    .Insert(0, KapacitorJsonContext.Default);
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            })
            .Build();

        // Register StopWatcher handler — server sends this to tell us to shut down
        hubConnection.On<string>("StopWatcher", reason => {
            Log($"Received StopWatcher signal: {reason}");
            cts.Cancel();
        });

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
            Log($"SignalR connection closed permanently: {ex?.Message}");
            // All reconnect attempts exhausted — self-terminate
            cts.Cancel();
            return Task.CompletedTask;
        };

        // Connect with retry (server may not be up yet) — try for up to 5 minutes
        var connectRetryDelay = TimeSpan.FromSeconds(1);
        var connectStartTime  = DateTimeOffset.UtcNow;
        while (!cts.Token.IsCancellationRequested) {
            try {
                await hubConnection.StartAsync(cts.Token);
                break;
            } catch (OperationCanceledException) {
                // SIGTERM/SIGINT during connect — exit gracefully
                break;
            } catch (Exception ex) when (DateTimeOffset.UtcNow - connectStartTime < TimeSpan.FromMinutes(5)) {
                Log($"SignalR connect failed, retrying in {connectRetryDelay.TotalSeconds}s: {ex.Message}");
                try {
                    await Task.Delay(connectRetryDelay, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }
                connectRetryDelay = TimeSpan.FromSeconds(Math.Min(connectRetryDelay.TotalSeconds * 2, 30));
            } catch (Exception ex) {
                // Timeout exhausted — give up
                Log($"SignalR connect failed after 5 minutes, giving up: {ex.Message}");
                await hubConnection.DisposeAsync();
                await logWriter.DisposeAsync();
                return 0;
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
        Log("Draining remaining lines...");
        await DrainNewLines(hubConnection, sessionId, transcriptPath, agentId, state, CancellationToken.None);

        // Signal drain complete to server
        try {
            if (hubConnection.State == HubConnectionState.Connected) {
                await hubConnection.InvokeAsync("WatcherDrainComplete", sessionId, agentId);
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
    static bool parseErrorLogged;

    static async Task DrainNewLines(HubConnection hubConnection, string sessionId, string transcriptPath, string? agentId, WatchState state, CancellationToken ct) {
        try {
            if (!File.Exists(transcriptPath)) return;

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
                if (lineIndex < state.LinesProcessed) { lineIndex++; continue; }
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
                if (state.LinesProcessed > 0 && !state.FullFileScanDone) {
                    Log("Scanning full file for first user text (resumed from later position)");
                    try {
                        await using var scanStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var scanReader = new StreamReader(scanStream);
                        while (await scanReader.ReadLineAsync(ct) is { } scanLine) {
                            if (string.IsNullOrWhiteSpace(scanLine)) continue;
                            var userText = TryExtractUserText(scanLine);
                            if (userText is null) continue;
                            SetFirstUserText(state, userText);
                            break;
                        }
                        state.FullFileScanDone = true;
                    } catch (Exception ex) {
                        Log($"Full file scan for user text failed: {ex.Message}");
                    }
                }

                // Also check new lines (normal path)
                if (state.FirstUserText is null && newLines.Count > 0) {
                    foreach (var line in newLines) {
                        var userText = TryExtractUserText(line);
                        if (userText is null) continue;
                        SetFirstUserText(state, userText);
                        break;
                    }
                }

                if (state.FirstUserText is not null)
                    Log($"First user text captured ({state.FirstUserText.Length} chars){(state.IsSlashCommand ? $", slash command: {state.SlashCommandName}" : "")}");
            }

            // Fire or retry title generation (runs even when no new lines arrive)
            if (state is { TitleGenerated: false, TitleInFlight: false } && agentId is null && state is { TitleAttempts: < 3, FirstUserText: not null }) {
                Log($"Triggering title generation (attempt {state.TitleAttempts + 1}/3)");
                state.TitleInFlight = true;
                state.TitleAttempts++;

                _ = state.IsSlashCommand ? PostTitleAsync(hubConnection, sessionId, $"Slash command: {state.SlashCommandName}", null, 0, 0, 0, 0, state) : GenerateTitleAsync(hubConnection, sessionId, state.FirstUserText, state);
            }

            if (newLines.Count == 0) {
                // No content lines to send — safe to advance past blank/whitespace lines
                state.LinesProcessed = linesRead;
                return;
            }

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            // Serialize repository payload to JSON string for the hub method
            var repoJson = repoToSend is not null
                ? JsonSerializer.Serialize(repoToSend, KapacitorJsonContext.Default.RepositoryPayload)
                : null;

            try {
                await hubConnection.InvokeAsync("SendTranscriptBatch", sessionId, agentId,
                    newLines.ToArray(), newLineNumbers.ToArray(), repoJson, ct);
                Log($"Sent {newLines.Count} line(s) via SignalR");

                // Only advance position after successful send — if send fails,
                // the next drain cycle will re-read and resend the same lines.
                // KurrentDB event IDs are deterministic (from transcript UUIDs),
                // so re-sending is idempotent.
                state.LinesProcessed = linesRead;

                if (repoToSend is not null)
                    state.LastSentRepository = repoToSend;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                Log($"SendTranscriptBatch failed, will retry from line {state.LinesProcessed}: {ex.Message}");
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
            state.IsSlashCommand   = true;
            state.SlashCommandName = cmdMatch.Groups[1].Value;
        }
        state.FirstUserText = userText;
    }

    static string? TryExtractUserText(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user")
                return null;

            // Skip system-injected meta messages (e.g. <local-command-caveat>)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True)
                return null;

            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content))
                return null;

            switch (content.ValueKind) {
                case JsonValueKind.String: {
                    var text = content.GetString();

                    // Skip local command output — not real user input
                    if (text is not null && text.StartsWith("<local-command-stdout>"))
                        return null;

                    return text;
                }
                // Handle array content (e.g., tool results with [{type:"text", text:"..."}])
                case JsonValueKind.Array: {
                    foreach (var element in content.EnumerateArray()) {
                        if (element.ValueKind                                            == JsonValueKind.Object
                         && element.TryGetProperty("type", out var t)   && t.GetString() == "text"
                         && element.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String) {
                            var text = txt.GetString();
                            if (text is not null && !text.StartsWith("<local-command-stdout>"))
                                return text;
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

    static async Task GenerateTitleAsync(HubConnection hubConnection, string sessionId, string userText, WatchState state) {
        try {
            var truncated = userText.Length > 500 ? userText[..500] : userText;
            var prompt = $"Generate a short descriptive title (max 10 words, no quotes, no period) for a coding session. The user's request: {truncated}";

            var result = await ClaudeCliRunner.RunAsync(prompt, TimeSpan.FromSeconds(15), Log);
            if (result is null) {
                state.TitleInFlight = false;
                return;
            }

            // Strip markdown formatting (bold, italic, code) from generated title
            var title = StripMarkdown(result.Result);

            // Sanity-check: limit to 120 chars
            if (title.Length > 120)
                title = title[..120];

            Log($"Title usage: model={result.Model} input={result.InputTokens} output={result.OutputTokens} cost=${result.CostUsd:F4}");

            await PostTitleAsync(hubConnection, sessionId, title,
                result.Model, result.InputTokens, result.OutputTokens,
                result.CacheReadTokens, result.CacheWriteTokens, state);
        } catch (Exception ex) {
            Log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    static async Task PostTitleAsync(
            HubConnection hubConnection, string sessionId, string title,
            string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens,
            WatchState state) {
        try {
            await hubConnection.InvokeAsync("SendTitle", sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
            Log($"Title generated: {title}");
            state.TitleGenerated = true;
        } catch (Exception ex) {
            Log($"Title send failed: {ex.Message}");
        }

        state.TitleInFlight = false;
    }

    static bool RepoPayloadChanged(RepositoryPayload? current, RepositoryPayload? lastSent) {
        if (current is null) return false;
        if (lastSent is null) return true;

        return current.Owner     != lastSent.Owner
            || current.RepoName  != lastSent.RepoName
            || current.Branch    != lastSent.Branch
            || current.PrNumber  != lastSent.PrNumber
            || current.PrUrl     != lastSent.PrUrl
            || current.PrTitle   != lastSent.PrTitle;
    }

    static string StripMarkdown(string text) {
        // Strip bold/italic markers, inline code backticks, and heading prefixes
        text = MarkdownRegex().Replace(text, "");
        return text.Trim();
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [watch] {message}");

    public static int CountFileLines(string path) {
        try {
            if (!File.Exists(path)) return 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;
            while (reader.ReadLine() is not null) count++;
            return count;
        } catch {
            return 0;
        }
    }

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Compiled)]
    private static partial Regex CommandNameRx();
    [GeneratedRegex("[*_`#]+")]
    private static partial Regex MarkdownRegex();
}
