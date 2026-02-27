using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace kapacitor;

static class WatchCommand {
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

        using var httpClient = new HttpClient();
        var       state      = new WatchState();

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
            state.LastRepoDetection = DateTimeOffset.UtcNow;
        }

        Log($"Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        // Resume from server's last recorded position to avoid re-sending lines
        state.LinesProcessed = await GetResumePosition(baseUrl, httpClient, sessionId, agentId, transcriptPath);
        Log($"Resuming from line {state.LinesProcessed}");

        try {
            while (!cts.Token.IsCancellationRequested) {
                // Periodically refresh repository info (every 60s)
                if (cwd is not null && DateTimeOffset.UtcNow - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
                    state.Repository        = await RepositoryDetection.DetectRepositoryAsync(cwd);
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                }

                await DrainNewLines(baseUrl, httpClient, sessionId, transcriptPath, agentId, state);

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
        await DrainNewLines(baseUrl, httpClient, sessionId, transcriptPath, agentId, state);
        Log($"Done. {state.LinesProcessed} total lines processed.");

        logWriter.Dispose();
        return 0;
    }

    static readonly System.Text.RegularExpressions.Regex CommandNameRegex = new(@"<command-name>(.*?)</command-name>", System.Text.RegularExpressions.RegexOptions.Compiled);

    static async Task DrainNewLines(string baseUrl, HttpClient httpClient, string sessionId, string transcriptPath, string? agentId, WatchState state) {
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
            while (await reader.ReadLineAsync() is { } line) {
                if (lineIndex < state.LinesProcessed) { lineIndex++; continue; }
                if (!string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }
                lineIndex++;
            }
            state.LinesProcessed = lineIndex;

            if (newLines.Count == 0) return;

            // Detect first user text for title generation (session watchers only, not agents)
            if (!state.TitleGenerated && !state.TitleInFlight && agentId is null && state.TitleAttempts < 3) {
                // Capture user text on first sight
                if (state.FirstUserText is null) {
                    foreach (var line in newLines) {
                        var userText = TryExtractUserText(line);
                        if (userText is null) continue;

                        var cmdMatch = CommandNameRegex.Match(userText);
                        if (cmdMatch.Success) {
                            state.IsSlashCommand  = true;
                            state.SlashCommandName = cmdMatch.Groups[1].Value;
                        }
                        state.FirstUserText = userText;
                        break;
                    }
                }

                // Fire title generation (or retry after previous failure)
                if (state.FirstUserText is not null) {
                    state.TitleInFlight = true;
                    state.TitleAttempts++;

                    if (state.IsSlashCommand) {
                        _ = PostTitleAsync(baseUrl, httpClient, sessionId, $"Slash command: {state.SlashCommandName}", null, 0, 0, 0, 0, state);
                    } else {
                        _ = GenerateTitleAsync(baseUrl, httpClient, sessionId, state.FirstUserText, state);
                    }
                }
            }

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            // POST batch to server
            var batch = new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = newLines.ToArray(),
                LineNumbers = newLineNumbers.ToArray(),
                Repository  = repoToSend
            };

            var       json        = JsonSerializer.Serialize(batch, kapacitor.KapacitorJsonContext.Default.TranscriptBatch);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            try {
                var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", httpContent);
                Log(resp.IsSuccessStatusCode ? $"Posted {newLines.Count} line(s)" : $"Server returned HTTP {(int)resp.StatusCode} for {newLines.Count} line(s)");

                if (resp.IsSuccessStatusCode && repoToSend is not null)
                    state.LastSentRepository = repoToSend;
            } catch (HttpRequestException ex) {
                Log($"Server unreachable after retries: {ex.Message}");
            }
        } catch (IOException ex) {
            Log($"Error reading file: {ex.Message}");
        }
    }

    static string? TryExtractUserText(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user")
                return null;

            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content))
                return null;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();
        } catch {
            // ignore parse errors
        }

        return null;
    }

    static async Task GenerateTitleAsync(string baseUrl, HttpClient httpClient, string sessionId, string userText, WatchState state) {
        try {
            var truncated = userText.Length > 500 ? userText[..500] : userText;
            var prompt = $"Generate a short descriptive title (max 10 words, no quotes, no period) for a coding session. The user's request: {truncated}";

            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName               = "claude",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            // Prevent the headless claude session from triggering kapacitor hooks (avoids infinite loop)
            psi.Environment["KAPACITOR_SKIP"] = "1";
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--max-turns");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add("haiku");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) {
                Log("Failed to start claude process for title generation");
                state.TitleInFlight = false;
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

                await process.WaitForExitAsync(cts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0) {
                    var stderrPreview = stderr.Length > 200 ? stderr[..200] : stderr;
                    Log($"Claude exited with code {process.ExitCode}: {stderrPreview}");
                    state.TitleInFlight = false;
                    return;
                }

                // Parse JSON output: { "result": "...", "model": "...", "usage": { ... }, "cost_usd": ... }
                string? title = null;
                string? model = null;
                long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;
                double? costUsd = null;

                try {
                    using var doc = JsonDocument.Parse(stdout);
                    var root = doc.RootElement;

                    title = root.TryGetProperty("result", out var r) ? r.GetString()?.Trim() : null;
                    model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                    costUsd = root.TryGetProperty("cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;

                    if (root.TryGetProperty("usage", out var usage)) {
                        inputTokens      = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt64() : 0;
                        outputTokens     = usage.TryGetProperty("output_tokens", out var outp) ? outp.GetInt64() : 0;
                        cacheReadTokens  = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt64() : 0;
                        cacheWriteTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt64() : 0;
                    }
                } catch (JsonException) {
                    // Fallback: treat stdout as plain text title
                    title = stdout.Trim();
                }

                if (string.IsNullOrWhiteSpace(title)) {
                    Log("Claude returned empty title");
                    state.TitleInFlight = false;
                    return;
                }

                // Sanity-check: limit to 120 chars
                if (title.Length > 120)
                    title = title[..120];

                Log($"Title usage: model={model} input={inputTokens} output={outputTokens} cost=${costUsd:F4}");

                await PostTitleAsync(baseUrl, httpClient, sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, state);
            } catch (OperationCanceledException) {
                Log("Title generation timed out (15s), killing process");
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                state.TitleInFlight = false;
            }
        } catch (Exception ex) {
            Log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    static async Task PostTitleAsync(
            string baseUrl, HttpClient httpClient, string sessionId, string title,
            string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens,
            WatchState state) {
        try {
            var payload = new SessionTitlePayload {
                SessionId       = sessionId,
                Title           = title,
                Model           = model,
                InputTokens     = inputTokens,
                OutputTokens    = outputTokens,
                CacheReadTokens = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens
            };
            var json    = JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.SessionTitlePayload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-title", content);

            if (resp.IsSuccessStatusCode) {
                Log($"Title generated: {title}");
                state.TitleGenerated = true;
            } else {
                Log($"Title POST failed: HTTP {(int)resp.StatusCode}");
            }
        } catch (Exception ex) {
            Log($"Title POST failed: {ex.Message}");
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

    public static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [watch] {message}");

    public static async Task<int> GetResumePosition(string baseUrl, HttpClient httpClient, string sessionId, string? agentId, string transcriptPath) {
        try {
            var query = agentId is not null ? $"?agentId={agentId}" : "";
            var resp  = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line{query}");

            if (resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent) {
                var json = await resp.Content.ReadAsStringAsync();
                var doc  = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("last_line_number", out var prop) && prop.ValueKind == JsonValueKind.Number)
                    return prop.GetInt32() + 1; // resume from the line after the last recorded one
            }
        } catch (HttpRequestException) {
            // Server unreachable — fall through to file-based skip
        } catch {
            // Parse error or other issue — fall through
        }

        // No line numbers from server (old data or unreachable): skip to current end of file
        return CountFileLines(transcriptPath);
    }

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
}