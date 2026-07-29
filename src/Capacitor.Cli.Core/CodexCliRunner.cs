using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// Headless invocation of the <c>codex exec</c> CLI for title generation and
/// what's-done summaries on Codex-vendor sessions.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ClaudeCliRunner"/>'s contract — same return record, same
/// timeout / cancellation semantics — but the wire is much simpler: <c>codex
/// exec</c> writes a single text response to <c>--output-last-message &lt;file&gt;</c>
/// which we read back as <see cref="ClaudeCliResult.Result"/>. Token usage and
/// cost are not currently parsed (would require <c>--json</c> event-stream
/// processing); they default to zero.
/// </remarks>
static class CodexCliRunner {
    public static async Task<ClaudeCliResult?> RunAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string?           model         = null,
            string            reasoning     = "low",
            CancellationToken ct            = default
        ) {
        // Fresh empty working dir per invocation: --skip-git-repo-check is
        // passed below so codex won't refuse to run, but a stable per-session
        // dir keeps any incidental writes (cache, temp probes) isolated and
        // sweepable.
        var workingDir        = Path.Combine(Path.GetTempPath(), $"kcap-codex-{Guid.NewGuid():N}");
        var lastMessageFile   = Path.Combine(workingDir, "last-message.txt");
        var createdWorkingDir = false;

        try {
            Directory.CreateDirectory(workingDir);
            createdWorkingDir = true;
        } catch (Exception ex) {
            log($"Failed to create isolated working directory: {ex.Message}");
            workingDir      = Path.GetTempPath();
            lastMessageFile = Path.Combine(workingDir, $"kcap-codex-last-{Guid.NewGuid():N}.txt");
        }

        try {
            return await RunCoreAsync(prompt, timeout, log, workingDir, lastMessageFile, model, reasoning, ct);
        } finally {
            if (createdWorkingDir) {
                try { Directory.Delete(workingDir, recursive: true); } catch {
                    /* best effort */
                }
            } else {
                try { File.Delete(lastMessageFile); } catch {
                    /* best effort */
                }
            }
        }
    }

    static async Task<ClaudeCliResult?> RunCoreAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string            workingDir,
            string            lastMessageFile,
            string?           model,
            string            reasoning,
            CancellationToken ct
        ) {
        // Resolve rather than pass "codex" verbatim: CreateProcess appends only .exe, so the
        // npm-installed codex.cmd shim on Windows would never be found (AI-72).
        var exePath = CliExecutable.Resolve("codex");

        if (exePath is null) {
            log("codex not found on PATH");

            return null;
        }

        var psi = new ProcessStartInfo {
            FileName               = exePath,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                // Symmetric with the Claude path — block any nested kcap hook
                // forwarding so a headless codex invocation can't re-trigger import.
                ["KCAP_SKIP"] = "1"
            }
        };
        // A globally-set OPENAI_API_KEY overrides ChatGPT subscription auth in `codex exec`.
        // Users on PAYG/API-key auth opt back in via profile flag or
        // KCAP_USE_PROVIDER_API_KEY=1.
        if (!ProviderApiKeyPolicy.ShouldKeepProviderKey()) {
            psi.Environment.Remove("OPENAI_API_KEY");
        }

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--ephemeral");
        psi.ArgumentList.Add("--ignore-user-config");
        psi.ArgumentList.Add("--ignore-rules");
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("read-only");
        psi.ArgumentList.Add("--color");
        psi.ArgumentList.Add("never");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"model_reasoning_effort=\"{reasoning}\"");
        // Only pin a model when the caller explicitly asks. Letting codex pick its
        // own default keeps title generation working for ChatGPT-account auth
        // (which rejects model names like "gpt-5.3-codex" with a 400).
        // Empty/whitespace is treated the same as null to match CodexLauncher's
        // argv-building behaviour — passing `-m ""` to codex is never useful.
        if (!string.IsNullOrWhiteSpace(model)) {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);
        }
        psi.ArgumentList.Add("--output-last-message");
        psi.ArgumentList.Add(lastMessageFile);
        // Read prompt from stdin via the literal "-" placeholder so titles longer
        // than the OS argv limit (and embedded special characters like "$" or
        // backticks) don't need escaping.
        psi.ArgumentList.Add("-");

        using var process = CliProcess.TryStart(psi, log);

        if (process is null) {
            log("Failed to start codex process");

            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), linkedCts.Token);
            await process.StandardInput.FlushAsync(linkedCts.Token);
            process.StandardInput.Close();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            throw;
        } catch (Exception ex) {
            log($"Failed to stream prompt to codex stdin: {ex.Message}");
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            return null;
        }

        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);
            await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0) {
                // Tail-truncate: codex prints a multi-line session header before any
                // error message lands on stderr, so head-truncation discarded the
                // useful part. Keep the last 800 chars so 4xx/5xx bodies
                // and skill-loader warnings further down still make it into the log.
                var stderrTail = stderr.Length > 800 ? "…" + stderr[^800..] : stderr;
                log($"Codex exited with code {process.ExitCode}, stderr: {SanitizeForLog(stderrTail)}");

                return null;
            }

            if (!File.Exists(lastMessageFile)) {
                log("Codex exited 0 but last-message file is missing");

                return null;
            }

            var resultText = (await File.ReadAllTextAsync(lastMessageFile, Encoding.UTF8, linkedCts.Token)).Trim();

            if (string.IsNullOrWhiteSpace(resultText)) {
                log("Codex produced an empty last message");

                return null;
            }

            // Token usage / cost are not parsed: would require --json event-stream
            // processing. Title and what's-done payloads tolerate zeros (they're
            // surfaced for diagnostics, not billing).
            return new ClaudeCliResult(
                Result: resultText,
                Model: model,
                InputTokens: 0,
                OutputTokens: 0,
                CacheReadTokens: 0,
                CacheWriteTokens: 0,
                CostUsd: null,
                NumTurns: 1
            );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            throw;
        } catch (OperationCanceledException) {
            log($"Codex process timed out ({timeout.TotalSeconds:0}s), killing");

            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            return null;
        }
    }

    /// <summary>
    /// Collapses newlines and drops other control characters so codex's multi-line
    /// stderr lands as a single log line — keeps the timestamp/prefix added by the
    /// caller's <c>log</c> aligned with the rest of the entry.
    /// </summary>
    static string SanitizeForLog(string value) {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value) {
            if (ch is '\r' or '\n') {
                sb.Append("\\n");
                continue;
            }

            if (char.IsControl(ch)) continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
