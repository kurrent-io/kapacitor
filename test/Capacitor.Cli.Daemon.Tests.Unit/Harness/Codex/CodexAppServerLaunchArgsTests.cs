using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The app-server argv reuses the exact same MCP-isolation passes as the PTY path (proven
/// byte-identical by the shared CodexLauncherTests) and adds the two app-server-only arms:
/// <c>--disable apps</c> and per-whitelisted-server <c>default_tools_approval_mode="approve"</c>.
/// Sandbox / approval / model / effort are per-turn protocol params on this transport, so none of
/// the PTY-only flags appear.</summary>
public class CodexAppServerLaunchArgsTests {
    [TempHome] public required TempHome Home { get; init; }

    CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" },
            TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [new("kcap-flows"), new("node_repl")]
        };

    static LauncherContext FlowCtx(string[]? allowlist = null) => new LauncherContext(
        AgentId: "agent-xyz",
        SourceRepoPath: "/tmp/repo",
        Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
        Prompt: null,
        Model: "gpt-5.3-codex",
        Effort: "high",
        Tools: null,
        IsReview: false,
        IsReviewFlow: true,
        Review: null,
        ReviewLaunch: null
    ) { McpAllowlist = allowlist };

    static string? DisableTableOverride(IReadOnlyList<string> args) =>
        args.FirstOrDefault(a => a.StartsWith("mcp_servers={", StringComparison.Ordinal));

    [Test]
    public async Task Disables_the_codex_apps_connector_runtime() {
        var args = NewLauncher().BuildAppServerLaunchArgs(FlowCtx());
        var i = args.ToList().IndexOf("--disable");
        await Assert.That(i).IsGreaterThan(-1);
        await Assert.That(args[i + 1]).IsEqualTo("apps");
    }

    [Test]
    public async Task Isolates_inherited_servers_and_whitelists_flow_result() {
        var args = NewLauncher().BuildAppServerLaunchArgs(FlowCtx());
        await Assert.That(DisableTableOverride(args)!).Contains("\"kcap-flows\"={enabled=false");
        await Assert.That(DisableTableOverride(args)!).Contains("\"node_repl\"={enabled=false");
        await Assert.That(args).Contains("mcp_servers.kcap-flow-result.enabled=true");
    }

    [Test]
    public async Task Preapproves_flow_result_server_tools() {
        var args = NewLauncher().BuildAppServerLaunchArgs(FlowCtx());
        await Assert.That(args).Contains("mcp_servers.kcap-flow-result.default_tools_approval_mode=\"approve\"");
    }

    [Test]
    public async Task Preapproves_each_allowlisted_server() {
        var args = NewLauncher().BuildAppServerLaunchArgs(FlowCtx(["kcap-sessions"]));
        await Assert.That(args).Contains("mcp_servers.kcap-sessions.enabled=true");
        await Assert.That(args).Contains("mcp_servers.kcap-sessions.default_tools_approval_mode=\"approve\"");
    }

    [Test]
    public async Task Omits_pty_only_flags() {
        // Sandbox/approval/model/effort/cwd are per-turn protocol params on this transport, not argv.
        var args = NewLauncher().BuildAppServerLaunchArgs(FlowCtx(["kcap-sessions"]));
        await Assert.That(args).DoesNotContain("--cd");
        await Assert.That(args).DoesNotContain("--sandbox");
        await Assert.That(args).DoesNotContain("--ask-for-approval");
        await Assert.That(args).DoesNotContain("--no-alt-screen");
        await Assert.That(args).DoesNotContain("-m");
        await Assert.That(string.Join(' ', args)).DoesNotContain("model_reasoning_effort");
        // app-server rejects the TUI-only hook-trust bypass flag — never emit it.
        await Assert.That(args).DoesNotContain("--dangerously-bypass-hook-trust");
    }

    [Test]
    public async Task Pty_path_never_gets_the_app_server_arms() {
        var pty = NewLauncher().BuildArgs(FlowCtx(["kcap-sessions"])).Args;
        await Assert.That(string.Join(' ', pty)).DoesNotContain("default_tools_approval_mode");
        await Assert.That(string.Join(' ', pty)).DoesNotContain("--disable");
    }
}
