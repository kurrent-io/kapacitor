using System.Diagnostics;
using System.Text.Json;

namespace kapacitor;

record ClaudeCliResult(
    string  Result,
    string? Model,
    long    InputTokens,
    long    OutputTokens,
    long    CacheReadTokens,
    long    CacheWriteTokens,
    double? CostUsd);

static class ClaudeCliRunner {
    /// <summary>
    /// Runs <c>claude -p &lt;prompt&gt; --output-format json --max-turns 1 --model haiku</c>
    /// and parses the JSON response. Returns null on failure (timeout, bad exit code, parse error).
    /// When the CLI returns an empty <c>result</c> field (known bug with extended thinking),
    /// falls back to reading the assistant response from the session transcript file.
    /// Logs are written via <paramref name="log"/>.
    /// </summary>
    public static async Task<ClaudeCliResult?> RunAsync(string prompt, TimeSpan timeout, Action<string> log) {
        var psi = new ProcessStartInfo {
            FileName               = "claude",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment            = {
                // Prevent the headless claude session from triggering kapacitor hooks (avoids infinite loop)
                ["KAPACITOR_SKIP"] = "1"
            }
        };
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add("haiku");
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");

        using var process = Process.Start(psi);
        if (process is null) {
            log("Failed to start claude process");
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);
        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0) {
                var stderrPreview = stderr.Length > 200 ? stderr[..200] : stderr;
                log($"Claude exited with code {process.ExitCode}: {stderrPreview}");
                return null;
            }

            var result = ParseResponse(stdout);
            if (result is not null) {
                return result;
            }

            // Fallback: CLI returned empty result (extended thinking bug).
            // Try reading the actual response from the session transcript file.
            var fallback = TryReadTranscriptFallback(stdout, log);
            if (fallback is not null) {
                log("Recovered result from session transcript (empty result workaround)");
                return fallback;
            }

            return null;
        } catch (OperationCanceledException) {
            log($"Claude process timed out ({timeout.TotalSeconds:0}s), killing");
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return null;
        }
    }

    internal static ClaudeCliResult? ParseResponse(string stdout) {
        try {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var result = root.TryGetProperty("result", out var r) ? r.GetString()?.Trim() : null;
            return string.IsNullOrWhiteSpace(result) ? null : BuildResult(root, result);
        } catch (JsonException) {
            // Fallback: treat stdout as plain text result
            var trimmed = stdout.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : new(trimmed, null, 0, 0, 0, 0, null);
        }
    }

    /// <summary>
    /// When the CLI JSON response has an empty <c>result</c> field, extract the session ID,
    /// find the transcript file, and read the last assistant text block as the result.
    /// </summary>
    static ClaudeCliResult? TryReadTranscriptFallback(string stdout, Action<string> log) {
        try {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            if (string.IsNullOrEmpty(sessionId)) {
                return null;
            }

            var transcriptPath = FindTranscriptFile(sessionId);
            if (transcriptPath is null) {
                log($"Transcript fallback: could not find {sessionId}.jsonl");
                return null;
            }

            var assistantText = ExtractLastAssistantText(transcriptPath);
            if (string.IsNullOrWhiteSpace(assistantText)) {
                log("Transcript fallback: no assistant text found in transcript");
                return null;
            }

            return BuildResult(root, assistantText);
        } catch (Exception ex) {
            log($"Transcript fallback failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Searches <c>~/.claude/projects/</c> for a transcript file matching the session ID.
    /// </summary>
    static string? FindTranscriptFile(string sessionId) {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectsDir = Path.Combine(home, ".claude", "projects");

        if (!Directory.Exists(projectsDir)) {
            return null;
        }

        var fileName = $"{sessionId}.jsonl";

        try {
            return Directory.EnumerateFiles(projectsDir, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads a transcript JSONL file and extracts the last assistant text block.
    /// </summary>
    static string? ExtractLastAssistantText(string transcriptPath) {
        string? lastText = null;

        foreach (var line in File.ReadLines(transcriptPath)) {
            if (string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            try {
                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "assistant"
                 && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                 && content.ValueKind == JsonValueKind.Array) {
                    foreach (var block in content.EnumerateArray()) {
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                         && block.TryGetProperty("text", out var txt)) {
                            var text = txt.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(text)) {
                                lastText = text;
                            }
                        }
                    }
                }
            } catch {
                // Skip unparseable lines
            }
        }

        return lastText;
    }

    /// <summary>
    /// Builds a <see cref="ClaudeCliResult"/> from the JSON response root and a result string.
    /// Extracts model and token metadata from the <c>modelUsage</c> field.
    /// </summary>
    static ClaudeCliResult BuildResult(JsonElement root, string result) {
        var costUsd = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDouble()
            : (double?)null;

        string? model            = null;
        long    inputTokens      = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;

        if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object) {
            foreach (var prop in modelUsage.EnumerateObject()) {
                model ??= prop.Name; // Use first model as the primary model name
                var mu = prop.Value;
                inputTokens      += mu.TryGetProperty("inputTokens", out var inp) ? inp.GetInt64() : 0;
                outputTokens     += mu.TryGetProperty("outputTokens", out var outp) ? outp.GetInt64() : 0;
                cacheReadTokens  += mu.TryGetProperty("cacheReadInputTokens", out var cr) ? cr.GetInt64() : 0;
                cacheWriteTokens += mu.TryGetProperty("cacheCreationInputTokens", out var cw) ? cw.GetInt64() : 0;
            }
        }

        return new(result, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd);
    }
}
