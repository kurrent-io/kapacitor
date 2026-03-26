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

        try {
            Log($"Generating what's-done summary for session {sessionId}");

            using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

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
            Log($"Recap text: {recapText.Length} chars");

            var prompt = "Based on the following session transcript, write a concise summary of what was accomplished. " +
                "Use bullet points. Focus on concrete changes and outcomes, not process details. "                       +
                "Keep it under 500 words.\n\n"                                                                           + recapText;

            var result = await ClaudeCliRunner.RunAsync(prompt, TimeSpan.FromSeconds(90), Log);

            if (result is null) {
                Log("Claude returned empty or failed");

                return 1;
            }

            Log($"Summary generated ({result.Result.Length} chars), model={result.Model}, input={result.InputTokens}, output={result.OutputTokens}");

            // 3. POST result back to server
            var payload = new WhatsDonePayload {
                SessionId        = sessionId,
                Content          = result.Result,
                Model            = result.Model,
                InputTokens      = result.InputTokens,
                OutputTokens     = result.OutputTokens,
                CacheReadTokens  = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens
            };

            var       payloadJson = JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.WhatsDonePayload);
            using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            try {
                var postResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/whats-done", httpContent);

                Log(
                    postResp.IsSuccessStatusCode
                        ? "Successfully posted what's-done summary"
                        : $"POST failed: HTTP {(int)postResp.StatusCode}"
                );
            } catch (HttpRequestException ex) {
                Log($"Server unreachable for POST: {ex.Message}");

                return 1;
            }

            return 0;
        } finally {
            await logWriter.DisposeAsync();
        }
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
                    sb.AppendLine($"- Write: {entry.FilePath ?? "unknown"}");

                    break;
                case "edit":
                    sb.AppendLine($"- Edit: {entry.FilePath ?? "unknown"}");

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

        return text.Length > 30_000 ? text[^30_000..] : text;
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [whats-done] {message}");
}
