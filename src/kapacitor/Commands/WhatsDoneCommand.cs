using System.Text;
using System.Text.Json;

namespace kapacitor.Commands;

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
            return await GenerateForSessionAsync(baseUrl, sessionId, Log);
        } finally {
            await logWriter.DisposeAsync();
        }
    }

    /// <summary>
    /// Core what's-done generation logic, callable without Console redirection.
    /// Uses the provided <paramref name="log"/> callback for diagnostics.
    /// </summary>
    public static async Task<int> GenerateForSessionAsync(string baseUrl, string sessionId, Action<string> log) {
        log($"Generating what's-done summary for session {sessionId}");

        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        // 1. Fetch session recap
        string recapText;

        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap");

            if (!resp.IsSuccessStatusCode) {
                log($"Failed to fetch recap: HTTP {(int)resp.StatusCode}");

                return 1;
            }

            var json    = await resp.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListRecapEntry);

            if (entries is null || entries.Count == 0) {
                log("No recap entries found, skipping summary generation");

                return 0;
            }

            recapText = FormatRecapAsText(entries);

            if (string.IsNullOrWhiteSpace(recapText)) {
                log("Recap text is empty after formatting, skipping");

                return 0;
            }
        } catch (HttpRequestException ex) {
            log($"Server unreachable: {ex.Message}");

            return 1;
        }

        // 2. Call claude -p to generate the summary
        log("Calling claude to generate summary...");
        log($"Recap text: {recapText.Length} chars");

        var prompt = """
            You are writing a knowledge base entry from a Claude Code session transcript.
            Other engineers and future AI sessions will read this to understand context
            that isn't obvious from the code diff alone.

            The transcript contains plans, user prompts, assistant responses, and file changes.
            The file changes themselves are already recorded separately — do NOT list them again.

            Write:

            **Context:** Why was this work done? What problem or request triggered it?

            **Key decisions:** Design choices, trade-offs, or alternatives that were considered
            and rejected. Only include decisions where the reasoning matters for future work.

            **Unfinished/Risks:** Anything deferred, left incomplete, or flagged as risky.
            Skip this section if everything was cleanly completed.

            Rules:
            - Under 300 words.
            - Don't list files or code changes — those are already recorded.
            - Don't describe process (retries, debugging, tool calls).
            - If a previous summary exists in the transcript, build on it — don't repeat it.

            Transcript:

            """ + recapText;

        var result = await ClaudeCliRunner.RunAsync(prompt, TimeSpan.FromSeconds(90), log);

        if (result is null) {
            log("Claude returned empty or failed");

            return 1;
        }

        log($"Summary generated ({result.Result.Length} chars), model={result.Model}, input={result.InputTokens}, output={result.OutputTokens}");

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
            using var postResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/whats-done", httpContent);

            log(
                postResp.IsSuccessStatusCode
                    ? "Successfully posted what's-done summary"
                    : $"POST failed: HTTP {(int)postResp.StatusCode}"
            );
        } catch (HttpRequestException ex) {
            log($"Server unreachable for POST: {ex.Message}");

            return 1;
        }

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
