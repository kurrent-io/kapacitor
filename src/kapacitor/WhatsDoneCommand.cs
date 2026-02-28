using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace kapacitor;

static class WhatsDoneCommand {
    public static async Task<int> HandleGenerateWhatsDone(string baseUrl, string sessionId) {
        // Redirect output to log file (same pattern as WatchCommand)
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "kapacitor", "logs");
        Directory.CreateDirectory(logDir);
        var logPath   = Path.Combine(logDir, $"{sessionId}-whatsdone.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        Log($"Generating what's-done summary for session {sessionId}");

        using var httpClient = new HttpClient();

        // 1. Fetch session recap
        string recapText;
        try {
            var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap");

            if (!resp.IsSuccessStatusCode) {
                Log($"Failed to fetch recap: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            var json    = await resp.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListRecapEntry);

            if (entries is null || entries.Count == 0) {
                Log("No recap entries found, skipping summary generation");
                return 0;
            }

            recapText = FormatRecapAsText(entries);

            if (string.IsNullOrWhiteSpace(recapText)) {
                Log("Recap text is empty after formatting, skipping");
                return 0;
            }
        } catch (HttpRequestException ex) {
            Log($"Server unreachable: {ex.Message}");
            return 1;
        }

        // 2. Call claude -p to generate the summary
        Log("Calling claude to generate summary...");

        var prompt = "Based on the following session transcript, write a concise summary of what was accomplished. " +
                     "Use bullet points. Focus on concrete changes and outcomes, not process details. " +
                     "Keep it under 500 words.\n\n" + recapText;

        var psi = new ProcessStartInfo {
            FileName               = "claude",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        psi.Environment["KAPACITOR_SKIP"] = "1";
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
            Log("Failed to start claude process");
            return 1;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0) {
                var stderrPreview = stderr.Length > 200 ? stderr[..200] : stderr;
                Log($"Claude exited with code {process.ExitCode}: {stderrPreview}");
                return 1;
            }

            // 3. Parse JSON response
            string? content = null;
            string? model   = null;
            long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;

            try {
                using var doc  = JsonDocument.Parse(stdout);
                var       root = doc.RootElement;

                content = root.TryGetProperty("result", out var r) ? r.GetString()?.Trim() : null;
                model   = root.TryGetProperty("model", out var m) ? m.GetString() : null;

                if (root.TryGetProperty("usage", out var usage)) {
                    inputTokens      = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt64() : 0;
                    outputTokens     = usage.TryGetProperty("output_tokens", out var outp) ? outp.GetInt64() : 0;
                    cacheReadTokens  = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt64() : 0;
                    cacheWriteTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt64() : 0;
                }
            } catch (JsonException) {
                content = stdout.Trim();
            }

            if (string.IsNullOrWhiteSpace(content)) {
                Log("Claude returned empty content");
                return 1;
            }

            Log($"Summary generated ({content.Length} chars), model={model}, input={inputTokens}, output={outputTokens}");

            // 4. POST result back to server
            var payload = new WhatsDonePayload {
                SessionId        = sessionId,
                Content          = content,
                Model            = model,
                InputTokens      = inputTokens,
                OutputTokens     = outputTokens,
                CacheReadTokens  = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens
            };

            var payloadJson = JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.WhatsDonePayload);
            using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            try {
                var postResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/whats-done", httpContent);
                Log(postResp.IsSuccessStatusCode
                    ? "Successfully posted what's-done summary"
                    : $"POST failed: HTTP {(int)postResp.StatusCode}");
            } catch (HttpRequestException ex) {
                Log($"Server unreachable for POST: {ex.Message}");
                return 1;
            }
        } catch (OperationCanceledException) {
            Log("Claude process timed out (30s), killing");
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return 1;
        }

        logWriter.Dispose();
        return 0;
    }

    static string FormatRecapAsText(List<RecapEntry> entries) {
        var sb = new StringBuilder();

        foreach (var entry in entries) {
            switch (entry.Type) {
                case "plan":
                    sb.AppendLine("## Plan");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();
                    break;
                case "user_prompt":
                    sb.AppendLine("## User Prompt");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();
                    break;
                case "assistant_text":
                    sb.AppendLine("## Assistant");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();
                    break;
                case "write":
                    sb.AppendLine($"## Write {entry.FilePath ?? "unknown"}");
                    sb.AppendLine("(file content omitted for brevity)");
                    sb.AppendLine();
                    break;
                case "edit":
                    sb.AppendLine($"## Edit {entry.FilePath ?? "unknown"}");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();
                    break;
                case "whats_done":
                    sb.AppendLine("## What's Done (previous summary)");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();
                    break;
            }
        }

        // Truncate to avoid exceeding claude's input limits
        var text = sb.ToString();
        return text.Length > 50_000 ? text[..50_000] : text;
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [whats-done] {message}");
}
