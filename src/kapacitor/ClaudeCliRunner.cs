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
        double? CostUsd
    );

static class ClaudeCliRunner {
    /// <summary>
    /// Runs <c>claude -p &lt;prompt&gt; --output-format json --max-turns &lt;N&gt; --model &lt;model&gt;</c>
    /// with no tools, and parses the JSON response. Returns null on failure
    /// (timeout, bad exit code, parse error). When the CLI returns an empty
    /// <c>result</c> field (known bug with extended thinking), falls back to
    /// reading the assistant response from the session transcript file. Logs
    /// are written via <paramref name="log"/>.
    ///
    /// <para>
    /// <paramref name="model"/> defaults to <c>haiku</c> (suitable for cheap
    /// summarization like title generation). For judgment tasks like the
    /// eval command, pass a stronger model (e.g. <c>sonnet</c>).
    /// </para>
    ///
    /// <para>
    /// When <paramref name="promptViaStdin"/> is true, the prompt is streamed
    /// to the <c>claude</c> process via stdin instead of being passed as a
    /// command-line argument — required for prompts that would otherwise
    /// exceed OS argv limits (notably 32K on Windows), such as the eval
    /// command's embedded session trace.
    /// </para>
    ///
    /// <para>
    /// <paramref name="ct"/> cancels the invocation cooperatively: stdin
    /// writes, stdout/stderr reads, and <see cref="Process.WaitForExitAsync"/>
    /// all observe a token linked with the internal timeout. On external
    /// cancellation the subprocess is killed (not just awaited-off) and
    /// <see cref="OperationCanceledException"/> propagates to the caller so
    /// it can distinguish shutdown from the internal timeout (which is
    /// swallowed and surfaces as a null result, same as before).
    /// </para>
    /// </summary>
    public static async Task<ClaudeCliResult?> RunAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string            model          = "haiku",
            int               maxTurns       = 1,
            bool              promptViaStdin = false,
            CancellationToken ct             = default
        ) {
        // Run from a stable isolated directory to avoid loading project-specific plugins/config
        // that might interfere with the headless title generation session.
        // Uses a fixed path so Claude treats all invocations as the same "project" and doesn't
        // scatter transcripts across many per-invocation directories under ~/.claude/projects.
        var stableDir = PathHelpers.ConfigPath("claude-cwd");

        try {
            Directory.CreateDirectory(stableDir);
        } catch (Exception ex) {
            log($"Failed to create isolated working directory: {ex.Message}");
            stableDir = Path.GetTempPath();
        }

        return await RunCoreAsync(prompt, timeout, log, stableDir, model, maxTurns, promptViaStdin, ct);
    }

    static async Task<ClaudeCliResult?> RunCoreAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string            workingDir,
            string            model,
            int               maxTurns,
            bool              promptViaStdin,
            CancellationToken ct
        ) {
        var psi = new ProcessStartInfo {
            FileName               = "claude",
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = promptViaStdin,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                // Prevent the headless claude session from triggering kapacitor hooks (avoids infinite loop)
                ["KAPACITOR_SKIP"] = "1"
            }
        };
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.ArgumentList.Add("-p");
        if (!promptViaStdin) {
            // When piping stdin, `claude -p` reads the prompt from stdin; don't
            // also pass it as a positional arg.
            psi.ArgumentList.Add(prompt);
        }
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add(maxTurns.ToString());
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");
        // `--tools ""` is not enough for headless single-turn judge runs:
        //   - MCP servers from the user's global config still load, so we
        //     need `--strict-mcp-config` (with no `--mcp-config`) to load zero.
        //   - The built-in `LSP` tool is attached regardless of `--tools`,
        //     and Claude eagerly probes any file paths it sees in the
        //     compacted trace, blowing past `--max-turns 1` with
        //     `stop_reason=tool_use`. `--disallowedTools LSP` blocks it.
        // Without both flags, real eval traces (which mention file paths)
        // fail every question with `error_max_turns`.
        psi.ArgumentList.Add("--strict-mcp-config");
        psi.ArgumentList.Add("--disallowedTools");
        psi.ArgumentList.Add("LSP");

        using var process = Process.Start(psi);

        if (process is null) {
            log("Failed to start claude process");

            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        // Link caller cancellation with the internal timeout so both flow into
        // the same awaited token. Distinguishing which fired (external vs
        // timeout) is cheap — ct.IsCancellationRequested is the source of truth.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (promptViaStdin) {
            try {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linkedCts.Token);
                await process.StandardInput.FlushAsync(linkedCts.Token);
                process.StandardInput.Close();
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // External cancel while writing stdin: kill the subprocess so
                // we don't leave claude running after the caller has given up,
                // then propagate so the daemon's cancellation path sees it.
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }

                throw;
            } catch (Exception ex) {
                log($"Failed to stream prompt to claude stdin: {ex.Message}");

                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }

                return null;
            }
        }

        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0) {
                var stderrPreview = stderr.Length > 200 ? stderr[..200] : stderr;
                var stdoutPreview = stdout.Length > 200 ? stdout[..200] : stdout;
                log($"Claude exited with code {process.ExitCode}, stderr: {stderrPreview}");

                if (!string.IsNullOrWhiteSpace(stdout)) {
                    log($"Claude stdout (exit {process.ExitCode}): {stdoutPreview}");
                }

                // Still try to parse stdout — claude sometimes exits 1 but produces a valid JSON result.
                // Use JSON-only parsing to avoid treating error text on stdout as a valid result.
                var errorResult = ParseJsonResponseOnly(stdout);

                if (errorResult is not null) {
                    log("Recovered result from stdout despite non-zero exit code");

                    return errorResult;
                }
            } else {
                var result = ParseResponse(stdout);

                if (result is not null) {
                    return result;
                }
            }

            // Fallback: try reading the actual response from the session transcript file.
            // Works both for empty result (extended thinking bug) and non-zero exit codes.
            var fallback = TryReadTranscriptFallback(stdout, log);

            if (fallback is not null) {
                log("Recovered result from session transcript (fallback)");

                return fallback;
            }

            return null;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // External cancellation wins over the internal timeout: kill the
            // subprocess (otherwise the await unblocks but claude keeps
            // running) and rethrow so the caller's cancellation path fires.
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            throw;
        } catch (OperationCanceledException) {
            log($"Claude process timed out ({timeout.TotalSeconds:0}s), killing");

            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            return null;
        }
    }

    internal static ClaudeCliResult? ParseResponse(string stdout) {
        var jsonResult = ParseJsonResponseOnly(stdout);

        if (jsonResult is not null) {
            return jsonResult;
        }

        // Fallback: treat stdout as plain text result (only safe when exit code was 0).
        // Don't treat JSON-shaped input as plain text — if ParseJsonResponseOnly returned null
        // for valid JSON, the result field was empty/missing and we should return null.
        var trimmed = stdout.Trim();

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed[0] is '{' or '[') {
            return null;
        }

        return new(trimmed, null, 0, 0, 0, 0, null);
    }

    /// <summary>
    /// Parses only valid JSON responses. Does not fall back to plain text.
    /// Safe to use on non-zero exit codes where stdout might contain error messages.
    /// </summary>
    static ClaudeCliResult? ParseJsonResponseOnly(string stdout) {
        try {
            using var doc  = JsonDocument.Parse(stdout);
            var       root = doc.RootElement;

            var result = root.Str("result")?.Trim();

            return string.IsNullOrWhiteSpace(result) ? null : BuildResult(root, result);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// When the CLI JSON response has an empty <c>result</c> field, extract the session ID,
    /// find the transcript file, and read the last assistant text block as the result.
    /// </summary>
    static ClaudeCliResult? TryReadTranscriptFallback(string stdout, Action<string> log) {
        try {
            using var doc  = JsonDocument.Parse(stdout);
            var       root = doc.RootElement;

            var sessionId = root.Str("session_id");

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
        var projectsDir = ClaudePaths.Projects;

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

                if (root.Str("type") == "assistant"
                 && root.Obj("message")?.Arr("content") is { } content) {
                    foreach (var block in content.EnumerateArray()) {
                        if (block.Str("type") == "text") {
                            var text = block.Str("text")?.Trim();

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

        string? model       = null;
        long    inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;

        if (root.Obj("modelUsage") is { } modelUsage) {
            foreach (var prop in modelUsage.EnumerateObject()) {
                model ??= prop.Name; // Use first model as the primary model name
                var mu = prop.Value;
                inputTokens      += mu.Num("inputTokens") ?? 0;
                outputTokens     += mu.Num("outputTokens") ?? 0;
                cacheReadTokens  += mu.Num("cacheReadInputTokens") ?? 0;
                cacheWriteTokens += mu.Num("cacheCreationInputTokens") ?? 0;
            }
        }

        return new(result, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd);
    }
}
