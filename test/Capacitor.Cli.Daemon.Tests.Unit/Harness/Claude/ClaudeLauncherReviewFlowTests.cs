using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudeLauncherReviewFlowTests {
    [TempHome] public required TempHome Home { get; init; }

    ClaudeLauncher NewLauncher(string? serverUrl = null, string capacitorPath = "kcap") =>
        new(new DaemonConfig { ClaudePath = "claude", ServerUrl = serverUrl ?? "", CapacitorPath = capacitorPath }, Home, NullLogger<ClaudeLauncher>.Instance);

    static LauncherContext NewCtx(bool isReviewFlow, string? prompt = "review this", string model = "sonnet") =>
        new(
            AgentId:       "a-1",
            SourceRepoPath:"/tmp/repo",
            Worktree:      new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt:        prompt,
            Model:         model,
            Effort:        null,
            Tools:         null,
            IsReview:      false,
            IsReviewFlow:  isReviewFlow,
            Review:        null,
            ReviewLaunch:  null
        );

    // review-detail branch (IsReview: true) — mirrors CodexLauncherTests' NewReviewCtx.
    static LauncherContext NewReviewCtx(string model) {
        var mcp = new ReviewLaunchBuilder.ReviewMcpServer(
            Command: "/opt/kcap",
            Args: ["mcp", "review", "--owner", "acme", "--repo", "widgets", "--pr", "42"],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = "https://srv" });

        return new(
            AgentId: "a-rev",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: model,
            Effort: null,
            Tools: null,
            IsReview: true,
            IsReviewFlow: false,
            Review: new ReviewLaunchInfo("acme", "widgets", 42),
            ReviewLaunch: new ReviewLaunchBuilder.ReviewLaunch(McpConfigPath: null, SystemPrompt: "Review PR acme/widgets#42", Mcp: mcp));
    }

    [Test]
    public async Task Review_flow_launch_bypasses_permissions() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--permission-mode");
        var i = Array.IndexOf(args, "--permission-mode");
        await Assert.That(args[i + 1]).IsEqualTo("bypassPermissions");
    }

    [Test]
    public async Task Review_flow_launch_without_server_url_loads_no_mcp_servers() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var parsed   = JsonNode.Parse(args[cfgIndex + 1])!.AsObject();
        await Assert.That(parsed["mcpServers"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Review_flow_launch_loads_exactly_the_flow_result_server() {
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(1);
        var flowResult = servers["kcap-flow-result"]!.AsObject();
        await Assert.That(flowResult["command"]!.GetValue<string>()).IsEqualTo("/opt/kcap");
        await Assert.That(flowResult["args"]![0]!.GetValue<string>()).IsEqualTo("mcp");
        await Assert.That(flowResult["args"]![1]!.GetValue<string>()).IsEqualTo("flow-result");
        await Assert.That(flowResult["env"]!["KCAP_URL"]!.GetValue<string>()).IsEqualTo("https://t.example");
        await Assert.That(flowResult["env"]!["KCAP_FLOW_AGENT_ID"]!.GetValue<string>()).IsEqualTo("a-1");
    }

    [Test]
    public async Task Review_flow_launch_disallows_the_agent_subagent_tool() {
        // Subagents don't inherit --mcp-config, so a spawned Agent could re-read ambient
        // MCP (incl. user-scoped kcap-flows) and recursively start a flow. Block it.
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--disallowedTools");
        var i = Array.IndexOf(args, "--disallowedTools");
        await Assert.That(args[i + 1]).IsEqualTo("Agent");
    }

    [Test]
    public async Task Review_flow_launch_still_passes_model_and_prompt() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, prompt: "the prompt", model: "opus")).Args;

        await Assert.That(args).Contains("--model");
        await Assert.That(args).Contains("opus");
        await Assert.That(args).Contains("--");
        await Assert.That(args[^1]).IsEqualTo("the prompt");
    }

    [Test]
    public async Task Non_review_flow_launch_is_unchanged_no_bypass_or_strict_mcp() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: false)).Args;

        await Assert.That(args).DoesNotContain("--permission-mode");
        await Assert.That(args).DoesNotContain("--strict-mcp-config");
        await Assert.That(args).DoesNotContain("--mcp-config");
        await Assert.That(args).DoesNotContain("--disallowedTools");
    }

    [Test]
    public async Task Claude_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }

    // === Required bugfix: "default" model sentinel must never reach the real `claude` binary ===
    // Mirrors CodexLauncher.AddModelArg's existing sentinel check. Every catalog reviewer vendor
    // override necessarily carries this sentinel (there is no model-override mechanism yet), so
    // this fix is load-bearing the moment a review flow ever launches Claude.

    [Test]
    [Arguments("default")]
    [Arguments("Default")]
    [Arguments("DEFAULT")]
    public async Task Review_flow_launch_omits_model_when_default_sentinel(string model) {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, model: model)).Args;

        await Assert.That(args).DoesNotContain("--model");
        await Assert.That(args).DoesNotContain(model);
    }

    [Test]
    public async Task Review_flow_launch_still_emits_model_when_concrete() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, model: "claude-sonnet-4-5")).Args;

        await Assert.That(args).Contains("--model");
        var i = Array.IndexOf(args, "--model");
        await Assert.That(args[i + 1]).IsEqualTo("claude-sonnet-4-5");
    }

    [Test]
    [Arguments("default")]
    [Arguments("Default")]
    [Arguments("DEFAULT")]
    public async Task Review_detail_launch_omits_model_when_default_sentinel(string model) {
        var args = NewLauncher().BuildArgs(NewReviewCtx(model)).Args;

        await Assert.That(args).DoesNotContain("--model");
        await Assert.That(args).DoesNotContain(model);
    }

    [Test]
    public async Task Review_detail_launch_still_emits_model_when_concrete() {
        var args = NewLauncher().BuildArgs(NewReviewCtx("claude-sonnet-4-5")).Args;

        await Assert.That(args).Contains("--model");
        var i = Array.IndexOf(args, "--model");
        await Assert.That(args[i + 1]).IsEqualTo("claude-sonnet-4-5");
    }

    // === D-c: definition MCP allowlist materialization ===

    [Test]
    public async Task ReviewFlow_with_allowlist_merges_servers_into_mcp_config() {
        var ctx = NewCtx(isReviewFlow: true) with { McpAllowlist = ["kcap-sessions"] };
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(ctx).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--disallowedTools");
        var dIdx = Array.IndexOf(args, "--disallowedTools");
        await Assert.That(args[dIdx + 1]).IsEqualTo("Agent");

        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(2);

        var flowResult = servers["kcap-flow-result"]!.AsObject();
        await Assert.That(flowResult["env"]!["KCAP_FLOW_AGENT_ID"]!.GetValue<string>()).IsEqualTo("a-1");

        var sessions = servers["kcap-sessions"]!.AsObject();
        await Assert.That(sessions["command"]!.GetValue<string>()).IsEqualTo("/opt/kcap");
        await Assert.That(sessions["args"]![0]!.GetValue<string>()).IsEqualTo("mcp");
        await Assert.That(sessions["args"]![1]!.GetValue<string>()).IsEqualTo("sessions");
        await Assert.That(sessions["env"]!["KCAP_URL"]!.GetValue<string>()).IsEqualTo("https://t.example");
        await Assert.That(sessions["env"]!.AsObject().ContainsKey("KCAP_FLOW_AGENT_ID")).IsFalse();
    }

    [Test]
    public async Task ReviewFlow_allowlist_strips_flow_starting_server_any_case() {
        var ctx = NewCtx(isReviewFlow: true) with { McpAllowlist = ["KCAP-Flows", "kcap-sessions"] };
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(ctx).Args;

        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(2);
        await Assert.That(servers.ContainsKey("kcap-flow-result")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-flows")).IsFalse();
    }

    [Test]
    public async Task ReviewFlow_allowlist_skips_unknown_names() {
        var ctxWithAllowlist = NewCtx(isReviewFlow: true) with { McpAllowlist = ["not-a-server"] };
        var ctxWithout       = NewCtx(isReviewFlow: true);
        var launcher         = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap");

        var argsWithAllowlist = launcher.BuildArgs(ctxWithAllowlist).Args;
        var argsWithout       = launcher.BuildArgs(ctxWithout).Args;

        var cfgIndexWith    = Array.IndexOf(argsWithAllowlist, "--mcp-config");
        var cfgIndexWithout = Array.IndexOf(argsWithout, "--mcp-config");

        await Assert.That(argsWithAllowlist[cfgIndexWith + 1]).IsEqualTo(argsWithout[cfgIndexWithout + 1]);
    }

    [Test]
    public async Task ReviewFlow_without_allowlist_args_byte_identical_to_today() {
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap")
            .BuildArgs(NewCtx(isReviewFlow: true)).Args;

        const string expectedMcpConfig =
            """{"mcpServers":{"kcap-flow-result":{"command":"/opt/kcap","args":["mcp","flow-result"],"env":{"KCAP_URL":"https://t.example","KCAP_FLOW_AGENT_ID":"a-1"}}}}""";

        string[] expected = [
            "--permission-mode", "bypassPermissions",
            "--strict-mcp-config",
            "--mcp-config", expectedMcpConfig,
            "--disallowedTools", "Agent",
            "--model", "sonnet",
            "--", "review this"
        ];

        await Assert.That(args).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReviewFlow_allowlist_with_null_element_skips_it_without_throwing() {
        // A wire-deserialized allowlist can contain a null element (e.g. a malformed JSON
        // array entry). It must flow through the existing unknown-name skip path rather
        // than throw a NullReferenceException out of the launch path.
        var ctx = NewCtx(isReviewFlow: true) with { McpAllowlist = [null!, "kcap-sessions"] };

        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(ctx).Args;

        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(2);
        await Assert.That(servers.ContainsKey("kcap-flow-result")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
    }

    [Test]
    public async Task ReviewFlow_empty_allowlist_args_byte_identical_to_null_allowlist() {
        var launcher = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap");

        var argsNull  = launcher.BuildArgs(NewCtx(isReviewFlow: true) with { McpAllowlist = null }).Args;
        var argsEmpty = launcher.BuildArgs(NewCtx(isReviewFlow: true) with { McpAllowlist = [] }).Args;

        await Assert.That(argsEmpty).IsEquivalentTo(argsNull, CollectionOrdering.Matching);
    }

    // === Phase A: read-only argv for a borrowed reviewer ===

    [Test]
    public async Task Review_flow_borrowed_cwd_denies_write_and_exec_tools_instead_of_bypass() {
        // A borrowed reviewer runs in the user's REAL checkout — bypassPermissions would
        // auto-approve writes there. Use an explicit deny-list instead of --permission-mode
        // plan (which reshapes tool use, incl. the injected flow-result MCP tool).
        var ctx  = NewCtx(isReviewFlow: true) with { Work = WorkLocation.BorrowedCwd };
        var args = NewLauncher().BuildArgs(ctx).Args;

        await Assert.That(args).DoesNotContain("--permission-mode");
        await Assert.That(args).DoesNotContain("bypassPermissions");
        await Assert.That(args).DoesNotContain("plan");

        await Assert.That(args).Contains("--disallowedTools");
        var i      = Array.IndexOf(args, "--disallowedTools");
        var denied = args[i + 1];

        foreach (var tool in new[] { "Agent", "Edit", "MultiEdit", "Write", "NotebookEdit", "Bash" }) {
            await Assert.That(denied).Contains(tool);
        }

        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--mcp-config");
    }

    [Test]
    public async Task Review_flow_borrowed_cwd_is_rejected_before_launcher_writes() {
        var ctx = NewCtx(isReviewFlow: true) with { Work = WorkLocation.BorrowedCwd };

        var ex = Assert.Throws<InvalidOperationException>(() => NewLauncher().Prepare(ctx));

        await Assert.That(ex.Message).Contains("owned review worktree");
    }

    [Test]
    public async Task Review_flow_owned_worktree_args_byte_identical_to_today() {
        // Regression: the owned path's argv must stay exactly as it is today.
        var ctx  = NewCtx(isReviewFlow: true) with { Work = WorkLocation.OwnedWorktree };
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(ctx).Args;

        const string expectedMcpConfig =
            """{"mcpServers":{"kcap-flow-result":{"command":"/opt/kcap","args":["mcp","flow-result"],"env":{"KCAP_URL":"https://t.example","KCAP_FLOW_AGENT_ID":"a-1"}}}}""";

        string[] expected = [
            "--permission-mode", "bypassPermissions",
            "--strict-mcp-config",
            "--mcp-config", expectedMcpConfig,
            "--disallowedTools", "Agent",
            "--model", "sonnet",
            "--", "review this"
        ];

        await Assert.That(args).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
