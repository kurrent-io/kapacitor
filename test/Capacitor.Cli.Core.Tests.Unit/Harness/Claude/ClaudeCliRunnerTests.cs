using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeCliRunnerTests {
    [Test]
    public async Task ParseResponse_ValidJsonWithAllFields_ParsesCorrectly() {
        const string json = """
                            {
                                "result": "Hello, world!",
                                "total_cost_usd": 0.0042,
                                "modelUsage": {
                                    "claude-haiku-3": {
                                        "inputTokens": 100,
                                        "outputTokens": 50,
                                        "cacheReadInputTokens": 30,
                                        "cacheCreationInputTokens": 10
                                    }
                                }
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsEqualTo("Hello, world!");
        await Assert.That(result.Model).IsEqualTo("claude-haiku-3");
        await Assert.That(result.InputTokens).IsEqualTo(100);
        await Assert.That(result.OutputTokens).IsEqualTo(50);
        await Assert.That(result.CacheReadTokens).IsEqualTo(30);
        await Assert.That(result.CacheWriteTokens).IsEqualTo(10);
        await Assert.That(result.CostUsd).IsEqualTo(0.0042);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ParseResponse_JsonWithEmptyOrWhitespaceResult_ReturnsNull(string resultValue) {
        var json = $$"""
                     {
                         "result": "{{resultValue}}",
                         "total_cost_usd": 0.001
                     }
                     """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseResponse_PlainText_FallsBackToTextResult() {
        const string plainText = "This is not JSON, just plain text.";

        var result = ClaudeCliRunner.ParseResponse(plainText);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsEqualTo(plainText);
        await Assert.That(result.Model).IsNull();
        await Assert.That(result.InputTokens).IsEqualTo(0);
        await Assert.That(result.OutputTokens).IsEqualTo(0);
        await Assert.That(result.CacheReadTokens).IsEqualTo(0);
        await Assert.That(result.CacheWriteTokens).IsEqualTo(0);
        await Assert.That(result.CostUsd).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\n")]
    public async Task ParseResponse_EmptyOrWhitespaceString_ReturnsNull(string input) {
        var result = ClaudeCliRunner.ParseResponse(input);

        await Assert.That(result).IsNull();
    }

    // DEV-1476: when --json-schema is active, the CLI fulfils the reply via
    // the StructuredOutput tool, leaves `result` empty, and puts the matched
    // object under the top-level `structured_output` field. The re-serialised
    // text must be surfaced as Result so downstream verdict/retrospective
    // parsers keep working unchanged.
    [Test]
    public async Task ParseResponse_StructuredOutputObject_IsReserialisedAsResult() {
        const string json = """
                            {
                                "result": "",
                                "structured_output": {"score": 5, "verdict": "pass"},
                                "total_cost_usd": 0.002,
                                "modelUsage": {
                                    "claude-haiku-4-5": {"inputTokens": 10, "outputTokens": 3}
                                }
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        // GetRawText preserves the source whitespace — what matters is the
        // result round-trips back to the same object, which is what
        // downstream ParseVerdict/ParseRetrospective do.
        using var doc = JsonDocument.Parse(result!.Result);
        await Assert.That(doc.RootElement.GetProperty("score").GetInt32()).IsEqualTo(5);
        await Assert.That(doc.RootElement.GetProperty("verdict").GetString()).IsEqualTo("pass");
        await Assert.That(result.Model).IsEqualTo("claude-haiku-4-5");
        await Assert.That(result.InputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task ParseResponse_StructuredOutputWinsOverEmptyResult() {
        // Belt-and-braces: even without `result` entirely, structured_output
        // alone is enough. (Real CLI replies always include `result` but
        // this guards against future format tweaks.)
        const string json = """
                            {"structured_output": {"overall": "clean run"}}
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        using var doc = JsonDocument.Parse(result!.Result);
        await Assert.That(doc.RootElement.GetProperty("overall").GetString()).IsEqualTo("clean run");
    }

    // DEV-1484 contract: when a caller opts into MCP mode, they must name the
    // tools they want exposed. `--strict-mcp-config` already limits which MCP
    // *servers* load to just the caller's inline config, and the
    // built-in lockdown (`--tools ""` / `--disallowedTools LSP`) stays on, so
    // an MCP config with no allowlist would load a server whose tools are never
    // permitted — the judge can't call anything, which is a silent
    // misconfiguration rather than a useful run. Requiring a non-empty
    // allowlist keeps the callable-tool surface explicit. Guard at the entry
    // point so the misuse surfaces as an ArgumentException.
    [Test]
    public async Task RunAsync_WithMcpConfigAndNullAllowedTools_Throws() =>
        await AssertAllowedToolsGuard(allowedTools: null);

    [Test]
    public async Task RunAsync_WithMcpConfigAndEmptyAllowedTools_Throws() =>
        await AssertAllowedToolsGuard(allowedTools: []);

    static async Task AssertAllowedToolsGuard(string[]? allowedTools) {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            ClaudeCliRunner.RunAsync(
                prompt:        "irrelevant",
                timeout:       TimeSpan.FromSeconds(1),
                log:           _ => { },
                mcpConfigJson: """{"mcpServers":{}}""",
                allowedTools:  allowedTools
            )
        );

        await Assert.That(ex?.ParamName).IsEqualTo("allowedTools");
    }

    // the tool-using (MCP) judge branch must keep `--strict-mcp-config`
    // on so claude loads ONLY the caller's inline session-scoped judge server.
    // Without it, a client machine with the `kcap` Claude Code plugin installed
    // leaks its global `kcap-sessions` server into the headless judge; the judge
    // calls those un-allowlisted tools and every call is blocked by permission
    // restrictions, degrading the verdict to "unable to investigate".
    [Test]
    public async Task BuildClaudeArgs_McpMode_IncludesStrictMcpConfig() {
        var args = ClaudeCliRunner.BuildClaudeArgs(
            prompt:         "irrelevant",
            promptViaStdin: true,
            model:          "sonnet[1m]",
            maxTurns:       15,
            jsonSchema:     null,
            mcpConfigJson:  """{"mcpServers":{"kcap-judge":{"command":"kcap","args":["mcp","judge"]}}}""",
            allowedTools:   ["mcp__kcap-judge__get_session_summary"],
            maxBudgetUsd:   1.0
        );

        await Assert.That(args).Contains("--strict-mcp-config");
    }

    [Test]
    public async Task BuildClaudeArgs_McpMode_PassesConfigAndAllowlist() {
        const string mcpConfig = """{"mcpServers":{"kcap-judge":{"command":"kcap"}}}""";

        var args = ClaudeCliRunner.BuildClaudeArgs(
            prompt:         "irrelevant",
            promptViaStdin: true,
            model:          "sonnet[1m]",
            maxTurns:       15,
            jsonSchema:     null,
            mcpConfigJson:  mcpConfig,
            allowedTools:   ["mcp__kcap-judge__get_session_summary", "mcp__kcap-judge__search_session"],
            maxBudgetUsd:   null
        );

        await Assert.That(FlagValue(args, "--mcp-config")).IsEqualTo(mcpConfig);
        await Assert.That(FlagValue(args, "--allowedTools"))
            .IsEqualTo("mcp__kcap-judge__get_session_summary,mcp__kcap-judge__search_session");
        // The built-in tool lockdown applies in MCP mode too. `--allowedTools`
        // alone does NOT stop claude exposing built-in tools like `Agent`
        // (subagents) to the model — a tools-judge will reach for `Agent`
        // instead of the MCP tools, spawning subagents that blow the
        // per-question budget and return no verdict. `--tools ""` disables the
        // built-in set; `--disallowedTools LSP` blocks the LSP probe that is
        // attached regardless of `--tools`. The allowlisted MCP tools are
        // layered on top via `--mcp-config` + `--allowedTools` and are NOT part
        // of the built-in set, so the lockdown does not remove them.
        await Assert.That(FlagValue(args, "--tools")).IsEqualTo("");
        await Assert.That(FlagValue(args, "--disallowedTools")).IsEqualTo("LSP");
    }

    [Test]
    public async Task BuildClaudeArgs_TextOnlyMode_LocksDownToolsAndMcp() {
        var args = ClaudeCliRunner.BuildClaudeArgs(
            prompt:         "irrelevant",
            promptViaStdin: true,
            model:          "haiku",
            maxTurns:       1,
            jsonSchema:     null,
            mcpConfigJson:  null,
            allowedTools:   null,
            maxBudgetUsd:   null
        );

        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--tools");
        await Assert.That(FlagValue(args, "--disallowedTools")).IsEqualTo("LSP");
        // No MCP config means no caller-supplied servers.
        await Assert.That(args).DoesNotContain("--mcp-config");
        await Assert.That(args).DoesNotContain("--allowedTools");
    }

    // The headless text-only tasks (title generation, what's-done summaries)
    // carry their full instructions in the user prompt, so the default Claude
    // Code system prompt is pure overhead — measured at ~8.2K prompt tokens per
    // title call vs ~2.4K with a minimal replacement. `--system-prompt` REPLACES
    // (not appends to) the default, so a tiny task-specific prompt strips that
    // overhead on the subscription path with no extra config.
    [Test]
    public async Task BuildClaudeArgs_WithSystemPrompt_ReplacesDefaultSystemPrompt() {
        const string sp = "You label coding-session transcripts. Output only the requested text.";

        var args = ClaudeCliRunner.BuildClaudeArgs(
            prompt:         "irrelevant",
            promptViaStdin: false,
            model:          "haiku",
            maxTurns:       1,
            jsonSchema:     null,
            mcpConfigJson:  null,
            allowedTools:   null,
            maxBudgetUsd:   null,
            systemPrompt:   sp
        );

        await Assert.That(FlagValue(args, "--system-prompt")).IsEqualTo(sp);
    }

    // A null/empty system prompt must leave the flag off entirely so callers
    // that don't opt in keep the CLI's default behaviour — passing
    // `--system-prompt ""` would wipe the system prompt to empty, not skip it.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task BuildClaudeArgs_WithoutSystemPrompt_OmitsFlag(string? systemPrompt) {
        var args = ClaudeCliRunner.BuildClaudeArgs(
            prompt:         "irrelevant",
            promptViaStdin: false,
            model:          "haiku",
            maxTurns:       1,
            jsonSchema:     null,
            mcpConfigJson:  null,
            allowedTools:   null,
            maxBudgetUsd:   null,
            systemPrompt:   systemPrompt
        );

        await Assert.That(args).DoesNotContain("--system-prompt");
    }

    /// <summary>Returns the argument immediately following <paramref name="flag"/>, or null.</summary>
    static string? FlagValue(List<string> args, string flag) {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    // the CLI returns is_error:true with the API failure text in
    // `result` when the upstream call fails (overload, rate limit, auth).
    // Surfacing that text as a title produced session titles like
    // "Claude API error: Overloaded". Treat it as a failure instead.
    [Test]
    public async Task ParseResponse_IsError_ReturnsNull() {
        const string json = """
                            {
                                "result": "Claude API error: Overloaded",
                                "is_error": true,
                                "subtype": "error_during_execution",
                                "total_cost_usd": 0.0
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseResponse_IsErrorWithStructuredOutput_ReturnsNull() {
        const string json = """
                            {
                                "result": "",
                                "structured_output": {"verdict": "pass"},
                                "is_error": true,
                                "subtype": "error_max_turns"
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseResponse_IsErrorFalse_StillParses() {
        const string json = """
                            {
                                "result": "ok",
                                "is_error": false
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsEqualTo("ok");
    }

    // Follow-up: if the envelope says is_error:true, the parser
    // returns null but RunCoreAsync still tries TryReadTranscriptFallback.
    // The transcript's last assistant block on a failed turn can be a
    // partial reply or stale auto-memory content, so converting that into
    // a successful result reintroduces the same regression. The fallback
    // must short-circuit on is_error before touching the filesystem.
    [Test]
    public async Task TryReadTranscriptFallback_IsError_ShortCircuits() {
        const string json = """
                            {
                                "session_id": "should-not-be-looked-up",
                                "is_error": true,
                                "result": "Claude API error: Overloaded"
                            }
                            """;
        var logs = new List<string>();

        var result = ClaudeCliRunner.TryReadTranscriptFallback(json, logs.Add);

        await Assert.That(result).IsNull();
        await Assert.That(logs).Contains(l => l.Contains("is_error", StringComparison.Ordinal));
    }

    [Test]
    public async Task ParseResponse_extracts_num_turns_from_json() {
        const string json = """
                            {
                                "result": "ok",
                                "num_turns": 4,
                                "total_cost_usd": 0.12,
                                "modelUsage": {
                                    "claude-sonnet-4-6": {
                                        "inputTokens": 10,
                                        "outputTokens": 5,
                                        "cacheReadInputTokens": 0,
                                        "cacheCreationInputTokens": 0
                                    }
                                }
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.NumTurns).IsEqualTo(4);
    }
}
