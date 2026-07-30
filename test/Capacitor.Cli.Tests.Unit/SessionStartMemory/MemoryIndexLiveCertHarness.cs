using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Shared scaffolding for the env-gated, per-harness "did the model actually receive the team-memory
/// index?" certifications. Every vendor's cert needs the same five things — a live gate, a throwaway
/// nonce memory, the real <c>disable_memory_index</c> profile flag, a bounded child process, and a
/// defensive answer parse — so they live here once instead of per vendor.
///
/// <para><b>Why these certs exist at all.</b> The unit suites prove the BYTES each adapter emits.
/// They cannot prove the harness surfaces those bytes to the model. Cursor IDE is the standing
/// counterexample: byte-perfect <c>additional_context</c>, model receipt not guaranteed. Only a real
/// turn distinguishes "we emitted it" from "the model got it".</para>
///
/// <para><b>Cost.</b> Nothing in CI: <see cref="SkipUnlessLiveGateReady"/> is the first statement in
/// every cert, so the skip happens before any process launch or HTTP call. A run spends real model
/// turns and touches the REAL server and the REAL profile config, so it is deliberately manual.</para>
///
/// <para>The two pre-existing certs (<c>ClaudeMemoryIndexLiveCertTests</c>,
/// <c>Cursor.CursorMemoryIndexLiveCertTests</c>) still carry their own private copies of this
/// scaffold. They are certified and gated — hence not exercised by CI — so they are deliberately left
/// alone here rather than refactored blind; migrating them is a follow-up.</para>
/// </summary>
internal static class MemoryIndexLiveCertHarness {
    public const string ServerUrlEnvVar = "KCAP_URL";

    static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The nonce shape every cert looks for. Deliberately distinctive so a match cannot be
    /// coincidental, and regex-free so the model is asked only to echo a literal.
    /// </summary>
    public static string NewNonce() => $"kcap-live-nonce-{Guid.NewGuid():N}";

    /// <summary>
    /// Asks the model to echo the nonce if — and only if — the injected index actually reached it.
    /// The nonce is embedded in the memory's DESCRIPTION, and the index injects one
    /// <c>slug: description</c> line per memory, so this is answerable from the injected context
    /// alone: no MCP memory server, no tool call, and no repo required. That keeps a failure
    /// attributable to injection rather than to tool wiring.
    /// </summary>
    public static string PositivePrompt =>
        "A block headed '## Team memory' may have been injected into your context. If it contains a "
      + "string of the form kcap-live-nonce- followed by 32 hex characters, reply with ONLY that "
      + "exact string and nothing else. If there is no such block or no such string, reply with "
      + "ONLY the word NONE.";

    /// <summary>
    /// The negative control's prompt. Same question, so a false positive can only come from the
    /// index genuinely being present — not from a differently-worded ask.
    /// </summary>
    public static string NegativePrompt => PositivePrompt;

    /// <summary>
    /// Gate. MUST be the first statement in every cert: it returns before any process launch, HTTP
    /// call, or memory write, which is what keeps CI spend at exactly zero.
    /// </summary>
    public static void SkipUnlessLiveGateReady(string liveGateEnvVar, string spendDescription, string preconditions) {
        Skip.Unless(
            Environment.GetEnvironmentVariable(liveGateEnvVar) == "1",
            $"Gated live model-receipt certification — set {liveGateEnvVar}=1 and {ServerUrlEnvVar}=<reachable kcap server> "
          + $"to run (spends {spendDescription}; requires {preconditions}, and `kcap login` already done against that server).");
        Skip.Unless(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerUrlEnvVar)),
            $"{ServerUrlEnvVar} must point at a reachable kcap server exposing GET /api/memories/index.");
    }

    public static string RequiredServerUrl() => Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;

    /// <summary>
    /// Bootstraps CLI config resolution, then returns the resolved server URL. **Every cert must call
    /// this before building an authenticated client.**
    ///
    /// <para>A cert runs inside the TEST assembly, not the `kcap` binary, so nothing has done what
    /// <c>Program.cs</c> does on startup: without it <c>AppConfig.ResolvedProfile</c> is null,
    /// credential resolution cannot find the profile's token, the request goes out unauthenticated,
    /// and the server answers <c>401</c> — even though `kcap whoami` succeeds in a shell a second
    /// earlier. That failure mode is indistinguishable from "you are not logged in", so it is worth
    /// naming: it is a missing bootstrap, not a missing login.</para>
    ///
    /// <para>Returns the URL the CLI itself resolves (honouring <c>KCAP_URL</c>, which the gate
    /// already requires) rather than the raw environment variable, so a cert talks to exactly the
    /// server the harness's own hook will talk to.</para>
    /// </summary>
    public static async Task<string> InitializeAndResolveServerUrlAsync() {
        var resolved = await AppConfig.ResolveServerUrl([]);

        return resolved ?? RequiredServerUrl();
    }

    /// <summary>
    /// Drives one <c>kcap mcp memory</c> tool call as a SUBPROCESS and returns its raw stdout.
    ///
    /// <para><b>Why a subprocess and not an in-process HttpClient.</b> This assembly redirects
    /// <c>KCAP_CONFIG_DIR</c> to a throwaway directory for its whole lifetime
    /// (<c>RepoPathStoreTests</c>'s <c>[Before(Assembly)]</c> hook), so in-process credential
    /// resolution reads an EMPTY config: every authenticated call 401s with "Not authenticated" even
    /// though `kcap whoami` succeeds in a shell a second earlier. A real `kcap` child reads the real
    /// config, so routing the memory lifecycle through the CLI is the only way a cert in this assembly
    /// can authenticate at all. It is also closer to what we are certifying — the same binary the
    /// harness hook invokes.</para>
    ///
    /// <para>Speaks just enough MCP: initialize, initialized, then one <c>tools/call</c>, all written
    /// up front. The server processes them in order and exits on stdin EOF, which is exactly the
    /// write-then-close shape <see cref="RunProcessAsync"/> already provides.</para>
    /// </summary>
    static async Task<string> CallMemoryToolAsync(string toolName, JsonObject arguments) {
        var stdin = string.Join('\n', [
            new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"]    = new JsonObject(),
                    ["clientInfo"]      = new JsonObject { ["name"] = "kcap-live-cert", ["version"] = "1" }
                }
            }.ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(),
            new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
            }.ToJsonString()
        ]) + "\n";

        // Interactive, NOT write-all-then-close: an MCP server dispatches per line and stops at stdin
        // EOF, so closing the stream up front raced the tools/call and only the initialize response
        // ever came back. Stdin stays open until the id-2 response is read, then the child is killed —
        // there is no point asking it to shut down cleanly when we already have the answer.
        var psi = new ProcessStartInfo("kcap") {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        psi.ArgumentList.Add("mcp");
        psi.ArgumentList.Add("memory");

        // The child MUST NOT inherit this assembly's redirected KCAP_CONFIG_DIR (RepoPathStoreTests
        // [Before(Assembly)]), or `kcap` reads the same empty throwaway config the in-process path did
        // and answers "Not logged in" — the 401 in a different costume. Removing it lets the child
        // resolve the real config, which is the whole point of going out-of-process.
        psi.Environment.Remove("KCAP_CONFIG_DIR");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `kcap mcp memory`.");
        OnProcessStarted?.Invoke(process.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        try {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync(cts.Token);

            while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line) {
                if (line.Length == 0 || line[0] != '{') continue;

                try {
                    if (JsonNode.Parse(line) is JsonObject frame && frame["id"]?.GetValue<int>() == 2) return line;
                } catch {
                    // Not a JSON-RPC frame — keep reading.
                }
            }

            throw new InvalidOperationException(
                $"`kcap mcp memory` {toolName} closed stdout before answering: {await process.StandardError.ReadToEndAsync(cts.Token)}");
        } finally {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }

    /// <summary>
    /// Saves a user-scoped, repo-independent ("global") memory whose description embeds the nonce.
    /// Global sidesteps needing a real repo hash for a throwaway worktree, and the description is
    /// where the nonce must live: the injected index carries <c>slug: description</c> lines, so this
    /// is what makes the cert answerable from injected context alone.
    /// </summary>
    public static async Task<string> SaveNonceMemoryAsync(string vendorLabel, string nonce) {
        var stdout = await CallMemoryToolAsync("save_memory", new JsonObject {
            ["audience"]    = "user",
            ["slug"]        = $"live-cert-{nonce}",
            ["description"] = $"kcap {vendorLabel} memory live-cert nonce: {nonce}",
            ["content"]     = $"kcap {vendorLabel} memory live-cert nonce: {nonce}. Safe to archive after the run.",
            ["kind"]        = "reference",
            ["global"]      = true
        });

        // An MCP tool result nests its payload as a JSON *string* inside result.content[].text, so the
        // id needs two parses, not a regex over the outer frame: the inner document's quotes arrive
        // "-escaped, so no pattern matching a literal `"memory_id"` will ever fire.
        //
        // Failing loudly (rather than returning null and archiving nothing) is what stops a cert
        // leaking its nonce memory into every later run's injected index.
        return ExtractMemoryId(stdout)
            ?? throw new InvalidOperationException($"save_memory returned no memory_id. stdout: {stdout}");
    }

    /// <summary>Digs the saved memory's id out of an MCP <c>tools/call</c> frame. Null if absent.</summary>
    internal static string? ExtractMemoryId(string frame) {
        try {
            var text = JsonNode.Parse(frame)?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            if (text is null) return null;

            var payload = JsonNode.Parse(text);

            return payload?["memory"]?["memory_id"]?.GetValue<string>()
                ?? payload?["memory_id"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Cleanup. A leaked cert memory is not cosmetic: it lands in the REAL injected index and every
    /// later cert then sees a stale nonce, so a positive test can pass on the wrong evidence.
    ///
    /// <para>The parameter is <c>id</c>, NOT <c>memory_id</c> — <c>save_memory</c> RETURNS
    /// <c>memory_id</c> and <c>archive_memory</c> ACCEPTS <c>id</c>, and sending the save's name back
    /// silently archived nothing. 13 cert memories leaked into the live index before that was caught,
    /// because the failure was swallowed here.</para>
    ///
    /// <para>Failures are therefore reported loudly AND verified: the tool's own <c>ok</c> is checked
    /// rather than assuming a returned frame means success. Still non-throwing — a cert must not fail
    /// on cleanup and mask its real verdict — but it can no longer fail in silence.</para>
    /// </summary>
    public static async Task ArchiveMemoryAsync(string vendorLabel, string memoryId) {
        try {
            var frame = await CallMemoryToolAsync("archive_memory", new JsonObject { ["id"] = memoryId });

            if (!ArchiveSucceeded(frame)) {
                await Console.Error.WriteLineAsync(
                    $"[{vendorLabel}-memory-live] LEAKED live-cert memory {memoryId} — archive_memory did not "
                  + $"confirm success. Archive it manually or later certs may read a stale nonce. Frame: {frame}");
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync(
                $"[{vendorLabel}-memory-live] LEAKED live-cert memory {memoryId} — archive threw: {ex.Message}");
        }
    }

    /// <summary>True only on an explicit success from the tool; an error frame or a missing/false
    /// <c>ok</c> both count as failure, so "no news" is never mistaken for "archived".</summary>
    internal static bool ArchiveSucceeded(string frame) {
        try {
            var result = JsonNode.Parse(frame)?["result"];
            if (result?["isError"]?.GetValue<bool>() == true) return false;

            var text = result?["content"]?[0]?["text"]?.GetValue<string>();

            return text is not null && JsonNode.Parse(text)?["ok"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Reads the active profile's <c>disable_memory_index</c> via <c>kcap config show</c>. Null when
    /// unset or unreadable. Callers restore what they read, so a run leaves the machine as it found it.
    /// </summary>
    public static async Task<bool?> ReadDisableMemoryIndexAsync() {
        var (exitCode, stdout, _) = await RunProcessAsync("kcap", ["config", "show"], workingDirectory: null);
        if (exitCode != 0) return null;

        try {
            var root          = JsonNode.Parse(ExtractLeadingJsonBlock(stdout));
            var activeProfile = root?["active_profile"]?.GetValue<string>();
            if (activeProfile is null) return null;

            return root?["profiles"]?[activeProfile]?["disable_memory_index"]?.GetValue<bool>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Isolates the leading JSON from <c>kcap config show</c> (JSON, blank line, then a "Path:"
    /// line). CRLF-tolerant: on Windows a \n-only split would return the whole blob and fail to parse.
    /// </summary>
    public static string ExtractLeadingJsonBlock(string stdout) =>
        stdout.Replace("\r\n", "\n").Split("\n\n", 2)[0];

    /// <summary>
    /// Restores the flag to what was observed. `kcap config` has no unset primitive, so an
    /// originally-absent (null) flag is restored as "false" — observably identical, since both read
    /// as not-disabled via `is true`.
    /// </summary>
    public static Task RestoreDisableMemoryIndexAsync(bool? original) =>
        SetDisableMemoryIndexAsync((original ?? false));

    public static async Task SetDisableMemoryIndexAsync(bool value) =>
        await RunProcessAsync("kcap", ["config", "set", "disable_memory_index", value ? "true" : "false"],
            workingDirectory: null);

    public static async Task RecordVersionAsync(string vendorLabel, string fileName, IReadOnlyList<string> args) {
        try {
            var (exitCode, stdout, _) = await RunProcessAsync(fileName, args, workingDirectory: null);
            await Console.Out.WriteLineAsync(
                $"[{vendorLabel}-memory-live] {fileName} {string.Join(' ', args)} (exit {exitCode}): {stdout.Trim()}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[{vendorLabel}-memory-live] could not record {fileName} version: {ex.Message}");
        }
    }

    /// <summary>Test-only seam: notified with the PID of every spawned process, so the cleanup test
    /// can confirm a timed-out child is actually gone. Never set by production code.</summary>
    internal static Action<int>? OnProcessStarted;

    /// <summary>
    /// Runs a child process with its output captured and a hard timeout, killing the whole tree on
    /// expiry so a hung harness CLI is never orphaned. <paramref name="stdin"/> is written and the
    /// stream closed when supplied — Codex reads its prompt that way.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory,
            TimeSpan? timeout = null, string? stdin = null) {
        var psi = new ProcessStartInfo(fileName) {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = stdin is not null,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Same reason as CallMemoryToolAsync: a `kcap config` child — and every harness CLI, which
        // invokes `kcap hook` itself — must see the REAL config, not this assembly's throwaway one.
        psi.Environment.Remove("KCAP_CONFIG_DIR");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        OnProcessStarted?.Invoke(process.Id);

        using var timeoutCts = new CancellationTokenSource(timeout ?? ProcessTimeout);
        try {
            if (stdin is not null) {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return (process.ExitCode, await stdoutTask, await stderrTask);
        } finally {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }

    /// <summary>
    /// Pulls the assistant's answer out of a harness CLI's stdout without assuming a shape: tries
    /// newline-delimited JSON, then a single JSON document, then falls back to the raw trimmed text.
    /// Each harness's real output shape is confirmed on its first live run and recorded on the cert.
    /// </summary>
    public static string ExtractAssistantAnswer(string stdout) {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return trimmed;

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (line.Length == 0 || line[0] is not ('{' or '[')) continue;
            try {
                if (JsonNode.Parse(line) is { } node && FindFirstTextField(node) is { } text) return text;
            } catch {
                // Not JSON — fall through.
            }
        }

        try {
            if (JsonNode.Parse(trimmed) is { } whole && FindFirstTextField(whole) is { } text) return text;
        } catch {
            // Plain text — return as-is.
        }

        return trimmed;
    }

    static string? FindFirstTextField(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var key in new[] { "text", "message", "content", "answer", "result" }) {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
                }
                foreach (var (_, child) in obj) {
                    if (FindFirstTextField(child) is { } nested) return nested;
                }
                return null;
            case JsonArray arr:
                foreach (var item in arr) {
                    if (FindFirstTextField(item) is { } nested) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Creates a throwaway working directory for a cert turn. It must be a fresh directory so the
    /// harness genuinely starts a NEW session and the sessionStart path under test actually fires.
    /// </summary>
    public static DirectoryInfo NewCertWorktree(string vendorLabel) =>
        Directory.CreateTempSubdirectory($"kcap-{vendorLabel}-memory-live-");
}
