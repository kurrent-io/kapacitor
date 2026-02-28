using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace kapacitor;

static class WatcherManager {
    static string GetWatcherDir() {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "kapacitor", "watchers");
    }

    static string GetPidFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.pid");

    public static void SpawnWatcher(string baseUrl, string key, string transcriptPath, string? agentId, string? sessionIdOverride = null, string? cwd = null) {
        try {
            var watcherDir = GetWatcherDir();
            Directory.CreateDirectory(watcherDir);

            // Resolve kapacitor binary path (same as current process)
            var kapacitorPath = Environment.ProcessPath ?? "kapacitor";
            var sessionId     = sessionIdOverride       ?? key;

            var arguments = agentId is not null
                ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
                : $"watch {key} \"{transcriptPath}\"";

            if (cwd is not null)
                arguments += $" --cwd \"{cwd}\"";

            var psi = new ProcessStartInfo(kapacitorPath, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KAPACITOR_URL"] = baseUrl
                }
            };

            var process = Process.Start(psi);
            if (process is null) {
                Console.Error.WriteLine($"Failed to spawn watcher for {key}");
                return;
            }

            // Close redirected streams from parent side so the child doesn't
            // hold the hook process's pipe FDs open (which would block Claude Code)
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            File.WriteAllText(GetPidFilePath(key), process.Id.ToString());
            Console.Error.WriteLine($"Spawned watcher for {key} (PID {process.Id})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn watcher for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills the watcher process for the given key. Returns true if the watcher was running and was killed,
    /// false if it was already dead or no PID file existed.
    /// </summary>
    public static async Task<bool> KillWatcher(string key) {
        var pidFile = GetPidFilePath(key);
        if (!File.Exists(pidFile)) return false;

        try {
            var pidText = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(pidText, out var pid)) {
                File.Delete(pidFile);
                return false;
            }

            try {
                var process = Process.GetProcessById(pid);

                // Send SIGTERM
                process.Kill(entireProcessTree: false);

                // Wait up to 5 seconds for graceful exit
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try {
                    await process.WaitForExitAsync(cts.Token);
                    Console.Error.WriteLine($"Watcher {key} (PID {pid}) exited gracefully");
                } catch (OperationCanceledException) {
                    // Force kill if it didn't exit in time
                    process.Kill(entireProcessTree: true);
                    Console.Error.WriteLine($"Watcher {key} (PID {pid}) force-killed after timeout");
                }

                return true;
            } catch (ArgumentException) {
                // Process already exited
                Console.Error.WriteLine($"Watcher {key} (PID {pid}) already exited");
                return false;
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"Error killing watcher {key}: {ex.Message}");
            return false;
        } finally {
            try { File.Delete(pidFile); } catch { /* ignore */ }
        }
    }

    static bool IsWatcherAlive(string key) {
        var pidFile = GetPidFilePath(key);
        if (!File.Exists(pidFile)) return false;

        try {
            var pidText = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(pidText, out var pid)) return false;

            try {
                var process = Process.GetProcessById(pid);
                return !process.HasExited;
            } catch (ArgumentException) {
                return false;
            }
        } catch {
            return false;
        }
    }

    public static void EnsureWatcherRunning(string baseUrl, string key, string transcriptPath, string? agentId, string? sessionIdOverride = null, string? cwd = null) {
        if (IsWatcherAlive(key)) return;

        // Watcher is dead or missing — respawn
        Console.Error.WriteLine($"Watcher {key} not running, respawning...");
        SpawnWatcher(baseUrl, key, transcriptPath, agentId, sessionIdOverride, cwd);
    }

    public static void SpawnWhatsDoneGenerator(string baseUrl, string sessionId) {
        try {
            var kapacitorPath = Environment.ProcessPath ?? "kapacitor";

            var psi = new ProcessStartInfo(kapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KAPACITOR_URL"] = baseUrl
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            var process = Process.Start(psi);
            if (process is null) {
                Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}");
                return;
            }

            // Close redirected streams from parent side so the child doesn't hold pipe FDs open
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            Console.Error.WriteLine($"Spawned what's-done generator for {sessionId} (PID {process.Id})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}: {ex.Message}");
        }
    }

    public static async Task InlineDrainAsync(string baseUrl, string sessionId, string transcriptPath, string? agentId) {
        try {
            using var httpClient = new HttpClient();

            // Get server's last recorded position
            int startLine;
            try {
                var query = agentId is not null ? $"?agentId={agentId}" : "";
                var resp  = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line{query}");

                if (resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent) {
                    var json = await resp.Content.ReadAsStringAsync();
                    var doc  = JsonDocument.Parse(json);
                    startLine = doc.RootElement.TryGetProperty("last_line_number", out var prop) && prop.ValueKind == JsonValueKind.Number
                        ? prop.GetInt32() + 1
                        : WatchCommand.CountFileLines(transcriptPath);
                } else {
                    startLine = WatchCommand.CountFileLines(transcriptPath);
                }
            } catch {
                startLine = WatchCommand.CountFileLines(transcriptPath);
            }

            if (!File.Exists(transcriptPath)) return;

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            var lineIndex = 0;
            while (await reader.ReadLineAsync() is { } line) {
                if (lineIndex < startLine) { lineIndex++; continue; }
                if (!string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }
                lineIndex++;
            }

            if (newLines.Count == 0) {
                Console.Error.WriteLine($"Inline drain for {sessionId}: no new lines to send");
                return;
            }

            var batch = new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = newLines.ToArray(),
                LineNumbers = newLineNumbers.ToArray()
            };

            var       batchJson = JsonSerializer.Serialize(batch, kapacitor.KapacitorJsonContext.Default.TranscriptBatch);
            using var content   = new StringContent(batchJson, Encoding.UTF8, "application/json");

            try {
                var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);
                Console.Error.WriteLine(resp.IsSuccessStatusCode
                    ? $"Inline drain for {sessionId}: sent {newLines.Count} line(s)"
                    : $"Inline drain for {sessionId}: server returned HTTP {(int)resp.StatusCode}");
            } catch (HttpRequestException ex) {
                Console.Error.WriteLine($"Inline drain for {sessionId}: server unreachable after retries — {ex.Message}");
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"Inline drain for {sessionId} failed: {ex.Message}");
        }
    }
}