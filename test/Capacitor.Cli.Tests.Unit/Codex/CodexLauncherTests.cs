using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Codex;

[NotInParallel("HomeEnvVarMutation")]
public class CodexLauncherTests {
    // Review-flow arg tests must be deterministic w.r.t. the developer's real
    // ~/.codex/config.toml, so the inherited-server seam defaults to empty. Tests that
    // exercise the isolation pass inject an explicit inherited list.
    static CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex" }, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => []
        };

    static LauncherContext NewCtx(
        string? prompt       = null,
        string  model        = "gpt-5.3-codex",
        string? effort       = null,
        bool    isReviewFlow = false
    ) => new(
        AgentId: "a-1",
        SourceRepoPath: "/tmp/repo",
        Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
        Prompt: prompt,
        Model: model,
        Effort: effort,
        Tools: null,
        IsReview: false,
        IsReviewFlow: isReviewFlow,
        Review: null,
        ReviewLaunch: null
    );

    // Real MCP isolation emits ONE `-c mcp_servers={ … }` TOML-value override that disables every
    // non-whitelisted inherited server (plain, dotted, or plugin-provided) uniformly. These helpers
    // locate that single override and assert membership without pinning the exact byte layout.
    static string? DisableTableOverride(string[] args) =>
        args.FirstOrDefault(a => a.StartsWith("mcp_servers={", StringComparison.Ordinal)
                              && !a.StartsWith("mcp_servers={}", StringComparison.Ordinal));

    static bool DisablesServer(string[] args, string name) =>
        DisableTableOverride(args) is { } table
        && table.Contains($"\"{name}\"={{enabled=false", StringComparison.Ordinal);

    [Test]
    public async Task BuildArgs_uses_never_approval_for_hosted_codex() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;
        await Assert.That(args).Contains("--sandbox");
        await Assert.That(args).Contains("workspace-write");
        await Assert.That(args).Contains("--ask-for-approval");
        await Assert.That(args).Contains("never");
        await Assert.That(args).DoesNotContain("on-request");
    }

    [Test]
    public async Task BuildArgs_uses_on_request_and_keeps_mcp_for_default_kind() {
        // Interactive (non review-flow) hosted Codex agents stay user-in-the-loop:
        // on-request approval and their configured MCP servers (no mcp_servers={} clear).
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--ask-for-approval");
        await Assert.That(args).Contains("on-request");
        await Assert.That(args).DoesNotContain("never");
        await Assert.That(string.Join(' ', args)).DoesNotContain("mcp_servers={}");
    }

    [Test]
    public async Task BuildArgs_maps_effort_max_to_xhigh() {
        var args = NewLauncher().BuildArgs(NewCtx(effort: "max")).Args;
        var joined = string.Join(' ', args);
        await Assert.That(joined).Contains("model_reasoning_effort=\"xhigh\"");
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task BuildArgs_passes_effort_through_unchanged(string effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).Contains($"model_reasoning_effort=\"{effort}\"");
    }

    [Test]
    [Arguments(null)]
    [Arguments("auto")]
    public async Task BuildArgs_omits_effort_when_null_or_auto(string? effort) {
        var args = NewLauncher().BuildArgs(NewCtx(effort: effort)).Args;
        await Assert.That(string.Join(' ', args)).DoesNotContain("model_reasoning_effort");
    }

    [Test]
    public async Task BuildArgs_appends_prompt_after_double_dash_when_present() {
        var args = NewLauncher().BuildArgs(NewCtx(prompt: "do a thing")).Args;
        var dashIdx = Array.IndexOf(args, "--");
        await Assert.That(dashIdx).IsGreaterThan(-1);
        await Assert.That(args[dashIdx + 1]).IsEqualTo("do a thing");
    }

    [Test]
    public async Task BuildArgs_emits_no_alt_screen_flag() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        await Assert.That(args).Contains("--no-alt-screen");
    }

    [Test]
    public async Task BuildArgs_includes_cd_with_worktree_path() {
        var args = NewLauncher().BuildArgs(NewCtx()).Args;
        var cdIdx = Array.IndexOf(args, "--cd");
        await Assert.That(cdIdx).IsGreaterThan(-1);
        await Assert.That(args[cdIdx + 1]).IsEqualTo("/tmp/wt");
    }

    [Test]
    public async Task BuildArgs_includes_model_when_set() {
        var args = NewLauncher().BuildArgs(NewCtx(model: "gpt-5.4")).Args;
        var mIdx = Array.IndexOf(args, "-m");
        await Assert.That(mIdx).IsGreaterThan(-1);
        await Assert.That(args[mIdx + 1]).IsEqualTo("gpt-5.4");
    }

    [Test]
    [Arguments("default")]
    [Arguments("Default")]
    [Arguments("DEFAULT")]
    public async Task BuildArgs_omits_model_when_default_sentinel(string model) {
        // "default" is a vendor-neutral sentinel meaning "use the vendor's own default
        // model". Codex has no such model — `-m default` fails on ChatGPT accounts
        // Omit -m so Codex falls back to its configured default.
        var args = NewLauncher().BuildArgs(NewCtx(model: model)).Args;
        await Assert.That(args).DoesNotContain("-m");
        await Assert.That(args).DoesNotContain(model);
    }

    [Test]
    public async Task BuildArgs_review_omits_model_when_default_sentinel() {
        var ctx  = NewReviewCtx("Review PR acme/widgets#42", "/opt/kcap", "https://srv", model: "default");
        var args = NewLauncher().BuildArgs(ctx).Args;
        await Assert.That(args).DoesNotContain("-m");
        await Assert.That(args).DoesNotContain("default");
    }

    [Test]
    public async Task BuildArgs_review_flow_does_not_emit_dead_mcp_servers_clear() {
        // The old `-c mcp_servers={}` "clear" is a no-op in Codex 0.144.3 (`-c` overrides
        // deep-merge, so an empty table removes nothing). It must be gone — real isolation is a
        // populated `mcp_servers={ "<name>"={enabled=false,…} }` disable table (only emitted when
        // there is something to disable; empty here since no inherited servers are injected).
        var args   = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;
        var joined = string.Join(' ', args);
        await Assert.That(joined).DoesNotContain("mcp_servers={}");
    }

    [Test]
    public async Task Review_flow_disables_inherited_servers_and_whitelists_only_flow_result() {
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            // The user has a hand-registered kcap-flows (start_review_flow) plus heavy servers —
            // exactly the recursion-guard threat the dead `mcp_servers={}` clear failed to strip.
            ReadInheritedMcpServerNames = static () => ["kcap-flows", "node_repl", "computer-use"]
        };
        var ctx = new LauncherContext(
            AgentId: "agent-xyz",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: true,
            Review: null,
            ReviewLaunch: null
        );

        var args = launcher.BuildArgs(ctx).Args;

        await Assert.That(string.Join(' ', args)).DoesNotContain("mcp_servers={}");

        // Every inherited server — including the hand-registered kcap-flows — is disabled in the
        // single table override.
        await Assert.That(DisablesServer(args, "kcap-flows")).IsTrue();
        await Assert.That(DisablesServer(args, "node_repl")).IsTrue();
        await Assert.That(DisablesServer(args, "computer-use")).IsTrue();

        // The ONLY server actually enabled/injected (via dotted overrides) is kcap-flow-result.
        var enabled = args.Where(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)
                                   && !a.Contains(".enabled=false")).ToArray();
        await Assert.That(enabled.All(a => a.StartsWith("mcp_servers.kcap-flow-result.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(enabled.Length).IsEqualTo(4);

        // Force-enabled regardless of anything the user's own config may say.
        await Assert.That(args).Contains("mcp_servers.kcap-flow-result.enabled=true");

        var command = enabled.Single(a => a.Contains(".command="));
        await Assert.That(command).Contains("/opt/kcap");
        var argsOverride = enabled.Single(a => a.Contains(".args="));
        await Assert.That(argsOverride).Contains("mcp");
        await Assert.That(argsOverride).Contains("flow-result");
        var env = enabled.Single(a => a.Contains(".env="));
        await Assert.That(env).Contains("KCAP_URL");
        await Assert.That(env).Contains("https://t.example");
        await Assert.That(env).Contains("KCAP_FLOW_AGENT_ID");
        await Assert.That(env).Contains("agent-xyz");
    }

    [Test]
    public async Task Review_flow_never_disables_a_server_it_is_about_to_whitelist() {
        // A user server literally named kcap-flow-result must NOT be disabled, or our own
        // injected submission server would be turned off.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => ["kcap-flow-result", "node_repl"]
        };
        var ctx = new LauncherContext(
            AgentId: "agent-xyz",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: true,
            Review: null,
            ReviewLaunch: null
        );

        var args = launcher.BuildArgs(ctx).Args;

        // kcap-flow-result must NOT appear in the disable table (it is whitelisted).
        await Assert.That(DisablesServer(args, "kcap-flow-result")).IsFalse();
        await Assert.That(DisablesServer(args, "node_repl")).IsTrue();
        await Assert.That(args.Any(a => a.StartsWith("mcp_servers.kcap-flow-result.command=", StringComparison.Ordinal))).IsTrue();

        // Not merely "not disabled" — explicitly forced on. The user's real
        // ~/.codex/config.toml is what `ReadInheritedMcpServerNames` is standing in for here,
        // and it may itself already carry `kcap-flow-result` as enabled=false from a prior
        // manual registration. Since `-c` deep-merges over that file, skipping our own disable
        // pass isn't enough — only an explicit enabled=true override guarantees the reviewer's
        // result-submission channel actually starts.
        await Assert.That(args).Contains("mcp_servers.kcap-flow-result.enabled=true");
    }

    [Test]
    public async Task Review_flow_force_enables_whitelisted_allowlist_server_even_if_inherited_as_disabled() {
        // Same bug as the flow-result server, on the allowlist-server path: the user's config
        // may already carry an allowlisted kcap server (e.g. kcap-sessions) as enabled=false —
        // `-c` deep-merges over that, so it must be forced back on, not merely left alone.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => ["kcap-sessions"]
        };

        var args = launcher.BuildArgs(NewFlowCtx(["kcap-sessions"])).Args;

        await Assert.That(DisablesServer(args, "kcap-sessions")).IsFalse();
        await Assert.That(args).Contains("mcp_servers.kcap-sessions.enabled=true");
    }

    [Test]
    public async Task Review_flow_disables_dotted_server_name() {
        // HIGH 2: a dotted/quoted server name (e.g. a hand-registered "corp.flows" that can start a
        // flow) cannot be expressed as a Codex `-c` dotted KEY path — the pre-hardening code logged
        // and LEFT it enabled, bypassing the recursion guard. The TOML-quoted key inside the single
        // table-value override targets it exactly, so it IS disabled now.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => ["corp.flows", "node_repl"]
        };
        var ctx = new LauncherContext(
            AgentId: "agent-xyz",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: true,
            Review: null,
            ReviewLaunch: null
        );

        var args = launcher.BuildArgs(ctx).Args;

        // The dotted server is disabled (not skipped), and its quoted key is emitted verbatim.
        await Assert.That(DisablesServer(args, "corp.flows")).IsTrue();
        await Assert.That(DisableTableOverride(args)!).Contains("\"corp.flows\"={enabled=false");
        await Assert.That(DisablesServer(args, "node_repl")).IsTrue();
        // No broken dotted-KEY override (which would fail Codex config load).
        await Assert.That(args).DoesNotContain("mcp_servers.corp.flows.enabled=false");
    }

    [Test]
    public async Task Review_flow_without_server_url_disables_inherited_and_injects_no_servers() {
        // No server URL → nothing is whitelisted (zero-server recursion-safe default), but the
        // inherited servers are STILL disabled so they don't leak into the reviewer.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => ["kcap-flows", "node_repl"]
        };
        var ctx = new LauncherContext(
            AgentId: "agent-xyz",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: true,
            Review: null,
            ReviewLaunch: null
        );

        var args = launcher.BuildArgs(ctx).Args;

        await Assert.That(args).DoesNotContain("mcp_servers={}");
        await Assert.That(DisablesServer(args, "kcap-flows")).IsTrue();
        await Assert.That(DisablesServer(args, "node_repl")).IsTrue();
        // Nothing enabled: no flow-result server injected.
        await Assert.That(args.Any(a => a.StartsWith("mcp_servers.kcap-flow-result.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Review_flow_reviewer_inherits_no_user_repo_or_plugin_mcp_server() {
        // HIGH 1 + HIGH 2 together: `codex mcp list --json` (the injected seam here) reports the
        // fully-composed effective list — user config.toml servers, a plugin-provided server, and a
        // dotted-name flow server. EVERY one must be disabled so the reviewer's effective MCP is
        // ONLY kcap-flow-result — no source (config, plugin, or dotted) leaks a flow-starting tool.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => [
                "kcap-flows",           // user config.toml, plain — the classic recursion vector
                "corp.flows",           // user config.toml, DOTTED (High 2)
                "sites-design-picker",  // native plugin-provided (High 1 — missed by config-only)
                "node_repl"             // plugin/config, benign
            ]
        };

        var args = launcher.BuildArgs(NewFlowCtx(null)).Args;

        // Every inherited server, regardless of source or name shape, is disabled.
        await Assert.That(DisablesServer(args, "kcap-flows")).IsTrue();
        await Assert.That(DisablesServer(args, "corp.flows")).IsTrue();
        await Assert.That(DisablesServer(args, "sites-design-picker")).IsTrue();
        await Assert.That(DisablesServer(args, "node_repl")).IsTrue();

        // The reviewer's ONLY enabled MCP server is kcap-flow-result.
        var enabled = args.Where(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)
                                   && !a.Contains(".enabled=false")).ToArray();
        await Assert.That(enabled.All(a => a.StartsWith("mcp_servers.kcap-flow-result.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(args).Contains("mcp_servers.kcap-flow-result.enabled=true");
    }

    [Test]
    public async Task Review_flow_fails_closed_when_inherited_servers_cannot_be_enumerated() {
        // FAIL-CLOSED: if the inherited MCP set can't be authoritatively enumerated (e.g. `codex mcp
        // list --json` fails), we must NOT proceed having disabled nothing — that would let a
        // hand-registered flow-starting server through. The enumeration seam throws, and BuildArgs
        // surfaces it as CodexReviewerMcpIsolationException for the orchestrator to reject the launch.
        var config = new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" };
        var launcher = new CodexLauncher(config, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () =>
                throw new CodexReviewerMcpIsolationException("mcp list failed")
        };

        await Assert.That(() => launcher.BuildArgs(NewFlowCtx(null)))
            .Throws<CodexReviewerMcpIsolationException>();
    }

    [Test]
    public async Task Prepare_overlays_codex_settings_dir_from_source_repo() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            var srcCodex = Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex")).FullName;
            File.WriteAllText(Path.Combine(srcCodex, "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            await Assert.That(File.Exists(Path.Combine(worktree, ".codex", "hooks.json"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_throws_when_no_hooks_json_anywhere() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            var ex = await Assert.ThrowsAsync<CodexHooksNotInstalledException>(async () => {
                NewLauncher().Prepare(ctx);
                await Task.CompletedTask;
            });
            await Assert.That(ex!.Message).Contains("kcap plugin install --codex");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_succeeds_when_user_scope_hooks_json_has_all_three_critical_events() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            await Assert.That(File.Exists(Path.Combine(home, ".codex", "config.toml"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_succeeds_when_project_scope_hooks_json_present_after_overlay() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(sourceRepo, ".codex"));
            File.WriteAllText(Path.Combine(sourceRepo, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);
            await Assert.That(File.Exists(Path.Combine(home, ".codex", "config.toml"))).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_invokes_codex_config_writer_with_worktree_path() {
        var sourceRepo = Directory.CreateTempSubdirectory("kcap-codexlauncher-src-").FullName;
        var worktree = Directory.CreateTempSubdirectory("kcap-codexlauncher-wt-").FullName;
        var home = Directory.CreateTempSubdirectory("kcap-codexlauncher-home-").FullName;
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);

        try {
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            File.WriteAllText(Path.Combine(home, ".codex", "hooks.json"), """
                {"hooks":{
                    "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                    "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
                }}
                """);

            var ctx = NewCtxWith(source: sourceRepo, worktree: worktree);
            NewLauncher().Prepare(ctx);

            var configToml = File.ReadAllText(Path.Combine(home, ".codex", "config.toml"));
            // The TOML writer emits the path as a basic (double-quoted) key, so
            // backslashes are escaped per spec (\ → \\). Match the escaped form
            // so the assertion holds on Windows too (no-op on POSIX paths).
            var escapedWorktree = worktree.Replace("\\", "\\\\");
            await Assert.That(configToml).Contains($"\"{escapedWorktree}\"");
            await Assert.That(configToml).Contains("trust_level = \"trusted\"");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Directory.Delete(sourceRepo, recursive: true);
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    static LauncherContext NewCtxWith(string source, string worktree) => new(
        AgentId: "a-1",
        SourceRepoPath: source,
        Worktree: new WorktreeInfo(Path: worktree, Branch: "br", SourceRepo: source),
        Prompt: null,
        Model: "gpt-5.3-codex",
        Effort: null,
        Tools: null,
        IsReview: false,
        IsReviewFlow: false,
        Review: null,
        ReviewLaunch: null
    );

    static LauncherContext NewReviewCtx(string prompt, string cliPath, string baseUrl, string model = "gpt-5.3-codex") {
        var mcp = new ReviewLaunchBuilder.ReviewMcpServer(
            Command: cliPath,
            Args: ["mcp", "review", "--owner", "acme", "--repo", "widgets", "--pr", "42"],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = baseUrl });

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
            ReviewLaunch: new ReviewLaunchBuilder.ReviewLaunch(McpConfigPath: null, SystemPrompt: prompt, Mcp: mcp));
    }

    [Test]
    public async Task BuildArgs_review_injects_kcap_review_mcp_server_via_config_overrides() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("Review PR acme/widgets#42", "/opt/kcap", "https://srv"));
        var joined = string.Join(' ', result.Args);

        await Assert.That(joined).Contains("mcp_servers.kcap-review.command=\"/opt/kcap\"");
        await Assert.That(joined).Contains("mcp_servers.kcap-review.args=[\"mcp\",\"review\",\"--owner\",\"acme\",\"--repo\",\"widgets\",\"--pr\",\"42\"]");
        await Assert.That(joined).Contains("mcp_servers.kcap-review.env={KCAP_URL=\"https://srv\"}");
    }

    [Test]
    public async Task BuildArgs_review_passes_prompt_after_double_dash_and_no_system_prompt() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("REVIEW PROMPT BODY", "/opt/kcap", "https://srv"));

        var dashIdx = Array.IndexOf(result.Args, "--");
        await Assert.That(dashIdx).IsGreaterThan(-1);
        await Assert.That(result.Args[dashIdx + 1]).IsEqualTo("REVIEW PROMPT BODY");
        await Assert.That(result.Args).DoesNotContain("--system-prompt");
    }

    [Test]
    public async Task BuildArgs_review_returns_null_mcp_config_path() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("p", "/opt/kcap", "https://srv"));
        await Assert.That(result.McpConfigPath).IsNull();
    }

    [Test]
    public async Task BuildArgs_review_toml_escapes_quotes_and_backslashes_in_command() {
        var ctx = NewReviewCtx("p", "C:\\Program Files\\kcap\\kcap.exe", "https://srv");
        var joined = string.Join(' ', NewLauncher().BuildArgs(ctx).Args);
        await Assert.That(joined).Contains("mcp_servers.kcap-review.command=\"C:\\\\Program Files\\\\kcap\\\\kcap.exe\"");
    }

    [Test]
    public async Task BuildArgs_review_toml_escapes_control_chars_in_command_args_and_env() {
        // TOML basic strings forbid raw control characters; an unescaped tab/newline/CR
        // produces invalid TOML and fails the Codex launch at config-parse time. Exercise
        // the encoder on all three injected surfaces: command, an args element, env value.
        var mcp = new ReviewLaunchBuilder.ReviewMcpServer(
            Command: "kcap\twith\tctrl",
            Args: ["mcp", "review", "line1\nline2"],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = "https://srv\r/x" });

        var ctx = new LauncherContext(
            AgentId: "a-rev",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: true,
            IsReviewFlow: false,
            Review: new ReviewLaunchInfo("acme", "widgets", 42),
            ReviewLaunch: new ReviewLaunchBuilder.ReviewLaunch(McpConfigPath: null, SystemPrompt: "p", Mcp: mcp));

        var joined = string.Join(' ', NewLauncher().BuildArgs(ctx).Args);

        await Assert.That(joined).Contains("mcp_servers.kcap-review.command=\"kcap\\twith\\tctrl\"");
        await Assert.That(joined).Contains("\"line1\\nline2\"");
        await Assert.That(joined).Contains("mcp_servers.kcap-review.env={KCAP_URL=\"https://srv\\r/x\"}");
    }

    [Test]
    public async Task Codex_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }

    // === D-c: definition MCP allowlist materialization ===

    static CodexLauncher NewFlowResultLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" }, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServerNames = static () => []
        };

    static LauncherContext NewFlowCtx(string[]? allowlist) => new LauncherContext(
        AgentId: "agent-xyz",
        SourceRepoPath: "/tmp/repo",
        Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
        Prompt: null,
        Model: "gpt-5.3-codex",
        Effort: null,
        Tools: null,
        IsReview: false,
        IsReviewFlow: true,
        Review: null,
        ReviewLaunch: null
    ) { McpAllowlist = allowlist };

    [Test]
    public async Task ReviewFlow_with_allowlist_merges_servers_into_dotted_overrides() {
        var args = NewFlowResultLauncher().BuildArgs(NewFlowCtx(["kcap-sessions"])).Args;

        await Assert.That(string.Join(' ', args)).DoesNotContain("mcp_servers={}");

        var dotted = args.Where(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)).ToArray();
        await Assert.That(dotted.Any(a => a.StartsWith("mcp_servers.kcap-flow-result.", StringComparison.Ordinal))).IsTrue();

        var sessionsDotted = dotted.Where(a => a.StartsWith("mcp_servers.kcap-sessions.", StringComparison.Ordinal)).ToArray();
        await Assert.That(sessionsDotted.Length).IsEqualTo(4);
        await Assert.That(args).Contains("mcp_servers.kcap-sessions.enabled=true");

        var command = sessionsDotted.Single(a => a.Contains(".command="));
        await Assert.That(command).Contains("/opt/kcap");
        var argsOverride = sessionsDotted.Single(a => a.Contains(".args="));
        await Assert.That(argsOverride).Contains("mcp");
        await Assert.That(argsOverride).Contains("sessions");
        var env = sessionsDotted.Single(a => a.Contains(".env="));
        await Assert.That(env).Contains("KCAP_URL");
        await Assert.That(env).Contains("https://t.example");
        await Assert.That(env).DoesNotContain("KCAP_FLOW_AGENT_ID");
    }

    [Test]
    public async Task ReviewFlow_allowlist_strips_flow_starting_server_any_case() {
        var args = NewFlowResultLauncher().BuildArgs(NewFlowCtx(["KCAP-Flows", "kcap-sessions"])).Args;

        var dotted = args.Where(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)).ToArray();
        await Assert.That(dotted.Any(a => a.StartsWith("mcp_servers.kcap-flows.", StringComparison.Ordinal))).IsFalse();
        await Assert.That(dotted.Any(a => a.StartsWith("mcp_servers.kcap-sessions.", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ReviewFlow_allowlist_skips_unknown_names() {
        var launcher = NewFlowResultLauncher();

        var argsWith    = launcher.BuildArgs(NewFlowCtx(["not-a-server"])).Args;
        var argsWithout = launcher.BuildArgs(NewFlowCtx(null)).Args;

        await Assert.That(argsWith).IsEquivalentTo(argsWithout, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReviewFlow_no_inherited_servers_emits_only_the_flow_result_whitelist() {
        // With no inherited servers there is nothing to disable — the dead `mcp_servers={}`
        // clear is gone and the args are exactly the flow-result whitelist.
        var args = NewFlowResultLauncher().BuildArgs(NewFlowCtx(null)).Args;

        string[] expected = [
            "--cd", "/tmp/wt",
            "--sandbox", "workspace-write",
            "--ask-for-approval", "never",
            "-c", "mcp_servers.kcap-flow-result.enabled=true",
            "-c", "mcp_servers.kcap-flow-result.command=\"/opt/kcap\"",
            "-c", "mcp_servers.kcap-flow-result.args=[\"mcp\",\"flow-result\"]",
            "-c", "mcp_servers.kcap-flow-result.env={KCAP_URL=\"https://t.example\",KCAP_FLOW_AGENT_ID=\"agent-xyz\"}",
            "-m", "gpt-5.3-codex",
            "--no-alt-screen"
        ];

        await Assert.That(args).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReviewFlow_empty_allowlist_args_byte_identical_to_null_allowlist() {
        var launcher = NewFlowResultLauncher();

        var argsNull  = launcher.BuildArgs(NewFlowCtx(null)).Args;
        var argsEmpty = launcher.BuildArgs(NewFlowCtx([])).Args;

        await Assert.That(argsEmpty).IsEquivalentTo(argsNull, CollectionOrdering.Matching);
    }

    // === Phase A: read-only sandbox for a borrowed reviewer ===

    [Test]
    public async Task BuildArgs_borrowed_cwd_uses_read_only_sandbox() {
        // A borrowed reviewer runs in the user's REAL checkout — never workspace-write.
        var ctx  = NewCtx(isReviewFlow: true) with { Work = WorkLocation.BorrowedCwd };
        var args = NewLauncher().BuildArgs(ctx).Args;

        await Assert.That(args).Contains("--sandbox");
        var sIdx = Array.IndexOf(args, "--sandbox");
        await Assert.That(args[sIdx + 1]).IsEqualTo("read-only");
        await Assert.That(args).DoesNotContain("workspace-write");

        await Assert.That(args).Contains("--ask-for-approval");
        await Assert.That(args).Contains("never");

        var cdIdx = Array.IndexOf(args, "--cd");
        await Assert.That(cdIdx).IsGreaterThan(-1);
        await Assert.That(args[cdIdx + 1]).IsEqualTo("/tmp/wt");
    }

    [Test]
    public async Task BuildArgs_owned_worktree_keeps_workspace_write_sandbox() {
        // Regression: the owned path's sandbox value must stay exactly as it is today.
        var ctx  = NewCtx(isReviewFlow: true) with { Work = WorkLocation.OwnedWorktree };
        var args = NewLauncher().BuildArgs(ctx).Args;

        var sIdx = Array.IndexOf(args, "--sandbox");
        await Assert.That(sIdx).IsGreaterThan(-1);
        await Assert.That(args[sIdx + 1]).IsEqualTo("workspace-write");
        await Assert.That(args).DoesNotContain("read-only");
    }
}
