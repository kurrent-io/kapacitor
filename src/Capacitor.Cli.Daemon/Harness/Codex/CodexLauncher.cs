using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

internal sealed partial class CodexLauncher(
        DaemonConfig           config,
        HarnessRegistry        harnesses,
        ILogger<CodexLauncher> logger
    ) : IHostedAgentLauncher {
    readonly CodexPaths _paths = harnesses.Of<CodexHarness>().Paths;

    public string Vendor  => "codex";
    public string CliPath => config.CodexPath;
    public bool   SupportsUnattended => true;
    public bool   SupportsBorrowedReviewFlow => true;

    // Approval prompts are off for review-flow launches (always `never`, any worktree) and for an
    // interactive launch whose caller-selected posture chose `never`. In both cases no dialog an
    // Enter could accept can appear, which is what gates the PTY submit strategy.
    public bool DisablesApprovalPrompts(LauncherContext ctx) =>
        ctx.IsReviewFlow || string.Equals(ctx.CodexPosture?.Approval, "never", StringComparison.Ordinal);
    public string BorrowedReviewContainment => "native-tool-clamp";

    /// <summary>Codex owns its own reviewer-model policy (known OpenAI/Codex slug families →
    /// slug-level equivalence key). Stateless singleton.</summary>
    public IReviewerModelResolver? ReviewerModelResolver => CodexReviewerModelResolver.Instance;

    // Codex supports its Windows sandbox on Windows 10 1809 (build 17763) and newer only;
    // Windows 11 is the recommended baseline. Older builds lack the APIs its restricted-token
    // and ACL boundaries rely on, so the CLI is unusable there even when installed.
    // https://developers.openai.com/codex/windows
    const int MinWindowsBuild = 17763;

    internal const string UnsupportedWindowsMessage =
        "Hosted Codex agents need Windows 10 1809 (build 17763) or newer — Windows 11 recommended. " +
        "Codex's Windows sandbox is unavailable on this host. "                                      +
        "See https://developers.openai.com/codex/windows";

    /// <summary>Non-Windows is always supported — the macOS/Linux path predates this gate.</summary>
    internal static bool WindowsVersionSupported =>
        !OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinWindowsBuild);

    // Version gate BEFORE the PATH probe, so an unsupported host never advertises the vendor at
    // all — the launch dialog hides Codex rather than offering a launch that cannot work.
    public bool IsAvailable() => WindowsVersionSupported && new CliResolver(config.Binaries).Exists(CliPath);

    /// <summary>
    /// Enumerates the effective MCP servers a review-flow reviewer would otherwise inherit —
    /// the recursion guard's foundation. Default runs <c>codex mcp list --json</c>
    /// (<see cref="CodexMcpInventory.ListInheritedServers"/>), which reports the fully-composed
    /// effective list (user <c>$CODEX_HOME/config.toml</c> <c>[mcp_servers]</c> AND active native
    /// plugins), honouring <c>CODEX_HOME</c> exactly as the spawned reviewer will. Reading
    /// <c>config.toml</c> alone (the pre-hardening behaviour) missed plugin-registered servers, so a
    /// flow-capable plugin server would never be disabled. Injectable so <see cref="BuildArgs"/>
    /// stays deterministic in unit tests. Throwing here is fail-closed — the launch is rejected
    /// rather than proceeding with an incomplete view of what the reviewer inherits.
    /// </summary>
    internal Func<IReadOnlyList<InheritedMcpServer>> ReadInheritedMcpServers { get; init; } =
        () => CodexMcpInventory.ListInheritedServers(config.CodexPath);

    /// <summary>Inert <c>command</c> stamped on a disabled TRANSPORT-LESS server's override. Codex
    /// requires a transport to be present to accept an <c>mcp_servers.&lt;name&gt;</c> override — a
    /// plugin-provided server has no transport at the config layer, so <c>enabled=false</c> alone
    /// fails config load with "invalid transport" (verified against 0.144.3). Supplying this sentinel
    /// satisfies the validator; it is never executed because the server is disabled, and Codex does
    /// not check that the command exists for a disabled server, so the value is cross-platform safe.
    /// NEVER stamped on a url-based server: <c>-c</c> deep-merges over <c>config.toml</c>, so a
    /// config-defined url server would end up with BOTH <c>url</c> and <c>command</c> and fail config
    /// load with "url is not supported for stdio" (verified against 0.144.6) — those
    /// re-state their own <c>url</c> instead.</summary>
    internal const string DisabledServerSentinelCommand = "kcap-review-flow-isolation-disabled";

    static readonly string[] CriticalHookEvents = ["SessionStart", "Stop", "PermissionRequest"];

    public void Prepare(LauncherContext ctx) {
        // Step 0: refuse an unsupported Windows build before touching the worktree. IsAvailable
        // normally keeps us off this path entirely; this catches an in-flight or stale-vendor-list
        // launch and turns it into an actionable LaunchFailed instead of a spawn error.
        if (!WindowsVersionSupported) throw new CodexUnsupportedWindowsException(UnsupportedWindowsMessage);

        // A borrowed cwd is the user's own repo: skip the repo-mutating steps (overlay,
        // ~/.codex trust write). Only the read-only hooks preflight runs for it.
        var owned = ctx.Work == WorkLocation.OwnedWorktree;

        if (owned) {
            // Step 1: overlay source/.codex into worktree FIRST so project-scope
            // hooks (kcap plugin install --codex --project) become visible to
            // the preflight in step 2. Best-effort.
            try {
                var sourceCodexDir = Path.Combine(ctx.SourceRepoPath, ".codex");

                if (Directory.Exists(sourceCodexDir)) {
                    FileSystemOverlay.OverlayDirectory(sourceCodexDir, Path.Combine(ctx.Worktree.Path, ".codex"));

                    // The overlay copies the SOURCE's whole .codex in, which re-materialises
                    // `.codex/config.toml` after worktree creation deliberately removed it — undoing the
                    // containment for every Codex launch. It matters where the source checkout is itself
                    // the untrusted branch: a borrowed snapshot (source is the user's cwd) or `--worktree`
                    // from a checkout already on that branch.
                    //
                    // Re-running the neutralizer is preferred over teaching the overlay an exclusion list:
                    // it reuses the one canonical path list, and the overlay's actual purpose is
                    // `.codex/hooks.json` (project-scope hooks; MCP servers are registered in the
                    // USER-scope ~/.codex/config.toml), so nothing this launcher needs is lost.
                    WorktreeManager.NeutralizeWorkspaceMcpConfig(ctx.Worktree.Path);
                }
            } catch (Exception ex) {
                LogOverlayFailed(ex, ctx.AgentId);
            }
        }

        // Step 2: hook preflight (fail-fast, read-only). Either worktree/cwd-scope OR
        // user-scope is sufficient. Runs for borrowed cwd too — it only reads.
        var worktreeHooks = Path.Combine(ctx.Worktree.Path, ".codex", "hooks.json");

        if (!HooksInstalledIn(worktreeHooks) && !HooksInstalledIn(_paths.UserHooksJson)) {
            throw new CodexHooksNotInstalledException(
                "Codex hooks not installed. Run `kcap plugin install --codex` " +
                "(user scope) or `kcap plugin install --codex --project` "      +
                "(project scope) and try again."
            );
        }

        if (owned) {
            // Step 3: pre-trust the worktree in ~/.codex/config.toml. Best-effort.
            try {
                CodexConfigWriter.TrustWorktree(ctx.Worktree.Path, _paths, logger);
            } catch (Exception ex) {
                LogTrustFailed(ex, ctx.AgentId);
            }
        }

        if (ctx.Tools is { Length: > 0 }) {
            LogToolsIgnoredForCodex(ctx.AgentId, ctx.Tools.Length);
        }
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        // Re-assert the orchestrator's guard for every non-interactive shape, BEFORE the PR-review
        // branch returns — a posture here means that guard was bypassed, and each of these launches
        // owes its posture to a containment rule: a borrowed cwd is the user's real checkout, a
        // review-flow reviewer has no human to answer a prompt, and PR review is fixed by contract.
        if (ctx.CodexPosture is not null
         && (ctx.IsReview || ctx.IsReviewFlow || ctx.Work == WorkLocation.BorrowedCwd)) {
            throw new InvalidOperationException(
                "codex_posture_not_overridable: a launch posture reached a borrowed, review-flow or PR-review launch");
        }

        if (ctx is { IsReview: true, ReviewLaunch: { } launch }) {
            return BuildReviewArgs(ctx, launch);
        }

        // Selected pair for an interactive owned-worktree launch; otherwise the derived containment
        // values — borrowed cwd is read-only (read-only is proven in the headless runner, including
        // with MCP injection, so the flow-result tool still works), and a review-flow reviewer never
        // pauses for approval while an interactive agent keeps the user in the loop.
        var (sandbox, approval) = CodexPosturePolicy.Resolve(ctx.Work, ctx.IsReviewFlow, ctx.CodexPosture);

        var args = new List<string> {
            "--cd",
            ctx.Worktree.Path,
            "--sandbox",
            sandbox,
            "--ask-for-approval",
            approval
        };

        // codex 0.146+ parks the interactive session on a "Hooks need review" prompt for any hook
        // it hasn't persisted trust for. A hosted launch has no one to answer it, so it hangs at
        // "Waiting for session to start". kcap installs and owns these hooks (it vets the source),
        // so bypass the per-invocation trust gate — same posture as the sandbox/approval flags
        // above. Global flag, so it also covers unattended review-flow reviewers.
        args.Add("--dangerously-bypass-hook-trust");

        // Review-flow reviewers get exactly ONE MCP server: kcap-flow-result (+ any
        // allowlisted, non-flow-starting server) — it can only submit a result, never start a
        // flow. Codex's `-c` overrides deep-merge into ~/.codex/config.toml (no analog of
        // Claude's `--strict-mcp-config`), so we (1) DISABLE every inherited server — from
        // config.toml AND native plugins, enumerated via `codex mcp list --json` — in one
        // `-c mcp_servers={ … }` table override that handles dotted/plugin names too, then
        // (2) force-enable exactly the whitelisted names with `enabled=true`. Otherwise a
        // reviewer inherits every user MCP server (including a hand-registered kcap-flows with
        // start_review_flow, vanishing the recursion guard), or — if the user's own config
        // already disabled a whitelisted name — starts without its result-submission channel.
        // Fail-closed: if the inherited set can't be enumerated, DisableInheritedMcpServers
        // throws and the launch is rejected rather than proceeding with nothing disabled.
        if (ctx.IsReviewFlow) {
            AppendMcpIsolationArgs(args, ctx, appServer: false);
        }

        AddModelArg(args, ctx);

        var effort = ctx.Effort;

        if (!string.IsNullOrEmpty(effort) && !string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
            var mapped = string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : effort;
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{mapped}\"");
        }

        args.Add("--no-alt-screen");

        if (!string.IsNullOrEmpty(ctx.Prompt)) {
            args.Add("--");
            args.Add(ctx.Prompt);
        }

        return new([.. args], McpConfigPath: null);
    }

    /// <summary>The shared MCP-isolation pass used by both transports: disable every inherited
    /// server, force-enable the flow-result server, materialize the allowlist. <paramref name="appServer"/>
    /// additionally stamps <c>default_tools_approval_mode="approve"</c> on each whitelisted server —
    /// required only for the <c>codex app-server</c> transport, where the first tool call otherwise
    /// raises an approval that wedges the turn even under <c>approvalPolicy: never</c> (the PTY TUI
    /// does not). Emitting them here (rather than duplicating the isolation in the app-server builder)
    /// keeps the two transports' overrides byte-identical apart from that one arm.</summary>
    void AppendMcpIsolationArgs(List<string> args, LauncherContext ctx, bool appServer) {
        DisableInheritedMcpServers(args, ctx);
        AddFlowResultServer(args, ctx, appServer);
        AddAllowlistServers(args, ctx, appServer);
    }

    /// <summary>Builds the argv passed to <c>codex app-server</c> (the tokens after the
    /// <c>app-server</c> subcommand) for a hosted review-flow reviewer: <c>--disable apps</c> — the
    /// <c>codex_apps</c> ChatGPT-connector runtime is not an <c>mcp_servers</c> entry and bypasses the
    /// disable table, so it must be turned off explicitly — plus the shared MCP-isolation <c>-c</c>
    /// overrides (with the per-whitelisted-server approval-mode arm). Sandbox, approval, model and
    /// effort are per-turn protocol parameters on the app-server transport, not argv, so none appear
    /// here.</summary>
    public IReadOnlyList<string> BuildAppServerLaunchArgs(LauncherContext ctx) {
        var args = new List<string> { "--disable", "apps" };

        if (ctx.IsReviewFlow) AppendMcpIsolationArgs(args, ctx, appServer: true);

        return args;
    }

    /// <summary>
    /// Real, fail-closed MCP isolation for a review-flow reviewer: disables EVERY server the
    /// reviewer would otherwise inherit — from the user's <c>$CODEX_HOME/config.toml</c> AND from
    /// active native plugins (both reported by <see cref="ReadInheritedMcpServers"/> via
    /// <c>codex mcp list --json</c>) — so only the servers we explicitly whitelist afterwards load.
    ///
    /// All disables go in ONE <c>-c mcp_servers={ … }</c> TOML-value override rather than per-server
    /// dotted keys, because:
    /// <list type="bullet">
    ///   <item>A dotted/quoted server name (e.g. <c>"corp.flows"</c>) cannot be expressed in Codex's
    ///     <c>-c</c> dotted-KEY path — it mis-splits and fails config load. A TOML-quoted key inside
    ///     the VALUE (<c>mcp_servers={"corp.flows"={…}}</c>) targets it exactly, so a dotted flow
    ///     server is disabled, not skipped (the pre-hardening code logged and LEFT it — the guard
    ///     bypass this fix closes).</item>
    ///   <item>A plugin-provided server has no transport at the config layer, so a bare
    ///     <c>enabled=false</c> fails config load with "invalid transport"; stamping the inert
    ///     <see cref="DisabledServerSentinelCommand"/> transport satisfies the validator while the
    ///     server stays off. A URL-BASED server must NOT get that sentinel: the deep-merge would
    ///     leave it with both <c>url</c> and <c>command</c>, which fails config load with "url is
    ///     not supported for stdio" — its override re-states the enumerated <c>url</c> as
    ///     the transport instead, which is valid whether the server came from config.toml (merges
    ///     onto the identical url) or a plugin (the config-layer entry then carries its own
    ///     transport).</item>
    ///   <item>Multiple separate <c>-c mcp_servers={…}</c> overrides do NOT accumulate (last wins),
    ///     whereas a single one deep-merges cleanly over the base file and composes with the dotted
    ///     whitelist ENABLE overrides added afterwards (all verified against Codex 0.144.3).</item>
    /// </list>
    ///
    /// Fail-closed: <see cref="ReadInheritedMcpServers"/> throws
    /// <see cref="CodexReviewerMcpIsolationException"/> when the inherited set cannot be
    /// authoritatively enumerated; that propagates out of <see cref="BuildArgs"/> and the
    /// orchestrator rejects the launch — we never proceed having disabled nothing.
    /// </summary>
    void DisableInheritedMcpServers(List<string> args, LauncherContext ctx) {
        var whitelisted = WhitelistedServerNames(ctx);

        var entries = new List<string>();

        foreach (var server in ReadInheritedMcpServers()) {
            if (string.IsNullOrEmpty(server.Name)) continue;
            if (whitelisted.Contains(server.Name)) continue;

            // TomlString both quotes and escapes, so ANY name — dotted, quoted, or containing
            // control chars — becomes a valid inline-table key that Codex resolves to exactly one
            // server. A url server keeps its own transport (deep-merging the sentinel command onto
            // it would fail config load); the sentinel transport makes plugin-provided
            // (transport-less) servers disable-able too.
            entries.Add(server.Url is { Length: > 0 } url
                ? $"{TomlString(server.Name)}={{enabled=false,url={TomlString(url)}}}"
                : $"{TomlString(server.Name)}={{enabled=false,command={TomlString(DisabledServerSentinelCommand)},args=[]}}");
        }

        if (entries.Count == 0) return;

        args.Add("-c");
        args.Add($"mcp_servers={{{string.Join(",", entries)}}}");
    }

    /// <summary>The MCP server names <see cref="AddFlowResultServer"/> +
    /// <see cref="AddAllowlistServers"/> will enable — the disable pass must never disable one of
    /// these. Empty when the daemon has no server URL / kcap path (nothing is whitelisted, so the
    /// disable pass strips everything — the recursion-safe default).</summary>
    HashSet<string> WhitelistedServerNames(LauncherContext ctx) {
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return set;

        set.Add("kcap-flow-result");

        foreach (var name in ctx.McpAllowlist ?? []) {
            var descriptor = KcapMcpRegistry.Resolve(name);

            if (descriptor is null || descriptor.StartsFlows) continue;

            set.Add(descriptor.Id);
        }

        return set;
    }

    /// <summary>Registers the reviewer-side result-submission server. Skipped (zero
    /// servers — the recursion-safe default) when the daemon has no server URL or kcap path;
    /// the reviewer then falls back to the transcript marker per the prompt contract.</summary>
    void AddFlowResultServer(List<string> args, LauncherContext ctx, bool appServer) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return;

        const string name = "kcap-flow-result";

        // Force-enable: the disable pass above skips this name, but the user's OWN
        // ~/.codex/config.toml may already have it (or never had it) set to enabled=false from
        // a prior manual registration. `-c` deep-merges over that file, so skipping the disable
        // is not enough on its own — an explicit enabled=true override wins regardless.
        args.Add("-c");
        args.Add($"mcp_servers.{name}.enabled=true");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.command={TomlString(config.CapacitorPath)}");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.args=[{TomlString("mcp")},{TomlString("flow-result")}]");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.env={{KCAP_URL={TomlString(config.ServerUrl)},KCAP_FLOW_AGENT_ID={TomlString(ctx.AgentId)}}}");
        AddAppServerApprovalMode(args, name, appServer);
    }

    /// <summary> D-c: materializes the flow definition's <see cref="LauncherContext.McpAllowlist"/>
    /// as additional dotted overrides, in the same clear-then-whitelist style as
    /// <see cref="AddFlowResultServer"/>. Each name resolves against the kcap-owned
    /// <see cref="KcapMcpRegistry"/> — never ambient user config — unknown names are skipped,
    /// and any flow-starting server is stripped regardless of listing (the recursion guard).
    /// Allowlist servers get KCAP_URL only — never KCAP_FLOW_AGENT_ID, which is exclusive to
    /// the flow-result submission channel. Skipped (same as the flow-result server) when the
    /// daemon has no server URL or kcap path configured.</summary>
    void AddAllowlistServers(List<string> args, LauncherContext ctx, bool appServer) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return;

        foreach (var name in ctx.McpAllowlist ?? []) {
            var descriptor = KcapMcpRegistry.Resolve(name);

            if (descriptor is null) {
                // name may be null here — a wire-deserialized allowlist element — so log it
                // defensively rather than let a null flow into the formatter unlabeled.
                LogAllowlistEntryUnknown(name ?? "(null)", ctx.AgentId);
                continue;
            }

            if (descriptor.StartsFlows) {
                LogAllowlistEntryStripped(name, ctx.AgentId);
                continue;
            }

            var id       = descriptor.Id;
            var argsList = string.Join(",", descriptor.Args.Select(TomlString));

            // Force-enable for the same reason as AddFlowResultServer: the user's own config
            // may already carry this name disabled, and `-c` only deep-merges over it.
            args.Add("-c");
            args.Add($"mcp_servers.{id}.enabled=true");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.command={TomlString(config.CapacitorPath)}");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.args=[{argsList}]");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.env={{KCAP_URL={TomlString(config.ServerUrl)}}}");
            AddAppServerApprovalMode(args, id, appServer);
        }
    }

    /// <summary>On the app-server transport only, pre-approve a whitelisted server's tools:
    /// <c>codex app-server</c> raises an approval on the FIRST tool call of an untrusted MCP server
    /// even under <c>approvalPolicy: never</c>, which would wedge an unattended reviewer's turn.
    /// <c>default_tools_approval_mode="approve"</c> suppresses it (<c>trust_level</c> does not).
    /// A no-op for the PTY transport.</summary>
    static void AddAppServerApprovalMode(List<string> args, string serverId, bool appServer) {
        if (!appServer) return;

        args.Add("-c");
        args.Add($"mcp_servers.{serverId}.default_tools_approval_mode=\"approve\"");
    }

    /// True when <paramref name="model"/> is a concrete model slug to pass through to Codex, rather than
    /// empty or the "default" no-override sentinel. "default" is the sentinel from the flow/agent
    /// dispatch; passing it to Codex verbatim — as PTY `-m default`, or as the app-server
    /// thread/turn `model` param — is rejected on a ChatGPT account ("The 'default' model is not
    /// supported when using Codex with a ChatGPT account") and yields a failed/empty turn. Omitting it
    /// makes Codex resolve the model from ~/.codex/config.toml (mirrors the effort=="auto" case). Shared
    /// by the PTY launch args AND <c>CodexAppServerHostedAgentRuntime</c>'s thread/turn params so the
    /// sentinel is honored on every Codex transport.
    internal static bool IsConcreteModel(string? model) =>
        !string.IsNullOrEmpty(model) && !string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);

    /// Append `-m &lt;model&gt;` unless the model is empty or the "default" no-override sentinel
    /// (see <see cref="IsConcreteModel"/>).
    void AddModelArg(List<string> args, LauncherContext ctx) {
        if (!IsConcreteModel(ctx.Model)) return;

        args.Add("-m");
        // A reviewer's model is pinned and priced by the server, which validates the slug it sent and
        // not whatever this host's config would swap it for — a round that reviewed under a
        // substituted model would carry an authority it does not have. A reviewer that parks on the
        // migration dialog instead is reaped by its first-output deadline, which is the visible
        // failure this trade prefers.
        args.Add(ctx.IsReviewFlow || ctx.IsReview ? ctx.Model! : MigratedModel(ctx.Model!, ctx.AgentId));
    }

    /// <summary>The slug Codex would end up on anyway, when its own
    /// <c>[notice.model_migrations]</c> maps the one asked for. Passing the retired slug instead
    /// raises a modal migration dialog before <c>thread/start</c>: nothing correlates the session
    /// while it is up, a parked TUI still renders so no stuck-launch watchdog fires, and the user sits
    /// on "Waiting for session to start…" until something answers it. Read from the operator's own
    /// acknowledged map, never a table of ours — Codex owns which model replaces which, and an
    /// unacknowledged migration is not in there to read.</summary>
    string MigratedModel(string model, string agentId) {
        var migrations = CodexConfigToml.ReadModelMigrations(_paths.ConfigToml);

        if (!migrations.TryGetValue(model, out var migrated)
         || string.IsNullOrWhiteSpace(migrated)
         || string.Equals(migrated, model, StringComparison.Ordinal))
            return model;

        LogModelMigrated(agentId, model, migrated);

        return migrated;
    }

    /// Review launch: inject the same kcap-review MCP server Claude gets, but via
    /// ephemeral `-c` overrides (no ~/.codex/config.toml mutation, nothing to clean
    /// up), and pass the rendered review prompt as Codex's initial prompt (Codex has
    /// no --system-prompt equivalent).
    ///
    /// Sandbox/approval stay FIXED here by design: the PR-review path sits outside the
    /// caller-posture seam, and a posture supplied with this launch kind is rejected upstream by
    /// CodexPosturePolicy rather than reaching this method.
    LaunchArgs BuildReviewArgs(LauncherContext ctx, ReviewLaunchBuilder.ReviewLaunch launch) {
        const string serverName = "kcap-review";
        var          mcp        = launch.Mcp;

        var args = new List<string> {
            "--cd",
            ctx.Worktree.Path,
            "--sandbox",
            "workspace-write",
            "--ask-for-approval",
            "on-request"
        };

        var argsList = string.Join(",", mcp.Args.Select(TomlString));
        var envList  = string.Join(",", mcp.Env.Select(kv => $"{kv.Key}={TomlString(kv.Value)}"));

        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.command={TomlString(mcp.Command)}");
        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.args=[{argsList}]");
        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.env={{{envList}}}");

        AddModelArg(args, ctx);

        args.Add("--no-alt-screen");
        args.Add("--");
        args.Add(launch.SystemPrompt);

        return new([.. args], McpConfigPath: null);
    }

    /// Encode a value as a TOML basic string: wrap in double quotes and escape
    /// backslashes, double quotes, and control characters. TOML basic strings forbid
    /// raw control chars, so an unescaped tab/newline/CR would yield invalid TOML and
    /// fail the Codex `-c` config parse. Covers Windows paths and arbitrary URLs.
    // Shared with the codex app-server argv builder so the two transports encode -c overrides
    // identically; the implementation lives in CodexToml.
    static string TomlString(string value) => CodexToml.String(value);

    /// Local launch: emit the mandatory daemon-level flags Codex always needs, then append
    /// the user's verbatim post-`--` args. A user duplicate of a mandatory flag is rejected
    /// outright (relying on Codex's arg precedence to make ours win is fragile).
    public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
        string[] mandatory = ["--cd", "--no-alt-screen"];

        foreach (var m in mandatory) {
            if (userArgs.Contains(m)) {
                throw new ArgumentException($"{m} is set by kcap and cannot be overridden in `agent start codex -- …`");
            }
        }

        // --cd sets the working dir; --no-alt-screen keeps the mirror/replay on the primary
        // screen. sandbox/approval defaults match the hosted path but stay user-overridable.
        var args = new List<string> {
            "--cd", ctx.Worktree.Path,
            "--sandbox", "workspace-write",
            "--ask-for-approval", "on-request",
            "--no-alt-screen"
        };
        args.AddRange(userArgs);

        return new([.. args], McpConfigPath: null);
    }

    public void Cleanup(AgentInstance agent) {
        // No-op: ~/.codex/config.toml trust entries are intentionally persistent.
    }

    static bool HooksInstalledIn(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;

        try {
            var root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject;

            return root is not null && CodexHooksParser.HasCapacitorHooksFor(root, CriticalHookEvents);
        } catch {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .codex settings for agent {AgentId} (continuing)")]
    partial void LogOverlayFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
    partial void LogTrustFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tools array of length {Count} ignored for vendor=codex (no allowlist concept) — agent {AgentId}")]
    partial void LogToolsIgnoredForCodex(string agentId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId}: codex retired model '{Requested}'; launching with '{Migrated}' from its own acknowledged migration map, which is what codex would resolve after prompting.")]
    partial void LogModelMigrated(string agentId, string requested, string migrated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' is not a kcap-owned server — skipping (agent {AgentId})")]
    partial void LogAllowlistEntryUnknown(string name, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' can start flows — stripped (agent {AgentId})")]
    partial void LogAllowlistEntryStripped(string name, string agentId);
}

/// <summary>
/// Codex-owned reviewer MODEL override policy. Recognizes the genuinely-known OpenAI/Codex model slug
/// families (<c>gpt-*</c>, the <c>o1</c>/<c>o3</c>/<c>o4</c> reasoning series, and <c>codex-*</c>) and
/// canonicalizes to a SLUG-level equivalence key (<c>codex/&lt;slug&gt;</c>). Unlike Claude, Codex slugs
/// are stable — there is no alias→dated resolution — so the requested slug and the concrete launched
/// slug match at the slug level, and the slug itself is the stable anchor. No central model table: this
/// policy lives entirely inside the Codex launcher.
/// </summary>
internal sealed class CodexReviewerModelResolver : IReviewerModelResolver {
    public static readonly CodexReviewerModelResolver Instance = new();

    CodexReviewerModelResolver() { }

    public string Vendor        => "codex";
    public string PolicyVersion => "codex-reviewer-model-v1";

    /// <summary>Genuinely-known OpenAI/Codex model-slug family prefixes. Recognizing by family prefix
    /// (rather than an exhaustive dated catalog) keeps this minimal while covering <c>gpt-5</c>,
    /// <c>gpt-5-codex</c>, <c>gpt-4.1</c>, the <c>o1</c>/<c>o3</c>/<c>o4</c> reasoning series, and
    /// <c>codex-mini-latest</c>. A slug matching a family prefix but not a real model still fails at
    /// launch (Codex rejects it), never a resolution-level false accept of another vendor's model.</summary>
    static readonly string[] KnownPrefixes = ["gpt-", "o1", "o3", "o4", "codex"];

    public ReviewerModelResolution Resolve(string requestedModel) {
        if (!ReviewerModelSyntax.IsWellFormed(requestedModel))
            return new(ReviewerModelDisposition.Invalid, DiagnosticCode: "malformed_model_id");

        var raw   = requestedModel.Trim();
        var lower = raw.ToLowerInvariant();

        var recognized = KnownPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal));
        if (!recognized)
            return new(ReviewerModelDisposition.Unavailable);

        // Codex slugs are stable (no alias→dated resolution), so the canonical slug itself is the
        // stable equivalence anchor — the requested slug and the concrete launched slug match at the
        // slug level.
        return new(
            ReviewerModelDisposition.Accept,
            CanonicalRequestedModel: lower,
            LaunchModel: raw,                   // passed through to the launcher verbatim
            EquivalenceKey: $"codex/{lower}");  // anchor
    }
}
