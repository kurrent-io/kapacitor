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

            // POST batch to server
            var batch = new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = newLines.ToArray(),
                LineNumbers = newLineNumbers.ToArray(),
                Repository  = state.Repository
            };

            var       json        = JsonSerializer.Serialize(batch, kapacitor.KapacitorJsonContext.Default.TranscriptBatch);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            try {
                var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", httpContent);
                Log(resp.IsSuccessStatusCode ? $"Posted {newLines.Count} line(s)" : $"Server returned HTTP {(int)resp.StatusCode} for {newLines.Count} line(s)");
            } catch (HttpRequestException ex) {
                Log($"Server unreachable after retries: {ex.Message}");
            }
        } catch (IOException ex) {
            Log($"Error reading file: {ex.Message}");
        }
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