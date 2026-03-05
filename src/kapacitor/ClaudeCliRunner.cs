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
    /// Logs are written via <paramref name="log"/>.
    /// </summary>
    public static async Task<ClaudeCliResult?> RunAsync(string prompt, TimeSpan timeout, Action<string> log) {
        var psi = new ProcessStartInfo {
            FileName               = "claude",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        // Prevent the headless claude session from triggering kapacitor hooks (avoids infinite loop)
        psi.Environment["KAPACITOR_SKIP"] = "1";
        psi.Environment["CLAUDECODE"] = null;
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add("haiku");

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

            return ParseResponse(stdout);
        } catch (OperationCanceledException) {
            log($"Claude process timed out ({timeout.TotalSeconds:0}s), killing");
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return null;
        }
    }

    static ClaudeCliResult? ParseResponse(string stdout) {
        try {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var result = root.TryGetProperty("result", out var r) ? r.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(result)) return null;

            var costUsd = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble()
                : (double?)null;

            string? model            = null;
            long    inputTokens      = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;

            // Extract model + tokens from modelUsage (keyed by model name, camelCase fields)
            if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object) {
                foreach (var prop in modelUsage.EnumerateObject()) {
                    model = prop.Name;
                    var mu = prop.Value;
                    inputTokens      = mu.TryGetProperty("inputTokens", out var inp) ? inp.GetInt64() : 0;
                    outputTokens     = mu.TryGetProperty("outputTokens", out var outp) ? outp.GetInt64() : 0;
                    cacheReadTokens  = mu.TryGetProperty("cacheReadInputTokens", out var cr) ? cr.GetInt64() : 0;
                    cacheWriteTokens = mu.TryGetProperty("cacheCreationInputTokens", out var cw) ? cw.GetInt64() : 0;
                    break; // Single-model call, take the first entry
                }
            }

            return new(result, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd);
        } catch (JsonException) {
            // Fallback: treat stdout as plain text result
            var trimmed = stdout.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : new(trimmed, null, 0, 0, 0, 0, null);
        }
    }
}
