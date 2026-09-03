using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Vendor-specific launch strategy. <see cref="AgentOrchestrator"/> handles
/// lifecycle concerns (PTY spawn, status, heartbeat, cleanup); each impl owns
/// the vendor-specific bits: CLI binary path, args, settings overlay, pre-trust,
/// MCP config, and per-vendor cleanup.
/// </summary>
internal interface IHostedAgentLauncher {
    /// <summary>Vendor token this launcher handles ("claude" or "codex").</summary>
    string Vendor { get; }

    /// <summary>Absolute path or bare command for the CLI. Pulled from DaemonConfig.</summary>
    string CliPath { get; }

    /// <summary>
    /// Reports whether <see cref="CliPath"/> resolves to an executable that
    /// looks installed. Used at daemon startup to build the list of
    /// supported vendors advertised over <c>DaemonConnect</c>, so the launch
    /// dialog only offers vendors the daemon can actually spawn.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Whether this vendor's launcher can host a fully UNATTENDED agent
    /// (LaunchKind.ReviewFlow): one that runs to completion with no human in
    /// the loop — no tool-permission prompts — and cannot recursively invoke
    /// flow-starting MCP tools. The orchestrator refuses an unattended launch
    /// for a vendor that returns <c>false</c>. Both shipped launchers
    /// support it; a future vendor (e.g. Gemini) may not.
    /// </summary>
    bool SupportsUnattended { get; }

    /// <summary>
    /// Whether this launch turns off interactive approval/permission prompts (codex
    /// <c>--ask-for-approval never</c>, claude <c>--permission-mode bypassPermissions</c>) — i.e.
    /// there is no dialog an Enter keypress could accept. The PTY runtime uses this to pick its
    /// submit strategy: the multi-CR spray (GitHub #349) is only safe when true; otherwise a single
    /// CR is sent so a stray Enter can't answer a live prompt. Must mirror the approval/permission
    /// flags this launcher's <see cref="BuildArgs"/> actually sets. Default false (assume prompts).
    /// </summary>
    bool DisablesApprovalPrompts(LauncherContext ctx) => false;

    /// <summary>Whether the launcher has a certified read-only borrowed-checkout review mode.</summary>
    bool SupportsBorrowedReviewFlow => false;

    string? BorrowedReviewContainment => null;

    /// <summary>
    /// This vendor's runtime-owned reviewer MODEL override policy, or <see langword="null"/> when the
    /// launcher has no authoritative model resolver yet. When non-null the daemon advertises
    /// <c>SupportsReviewerModelResolution</c> for this vendor and the server may drive a reviewer
    /// model override through it. A null resolver leaves the vendor's existing vendor-only unattended
    /// support intact; it simply is not an override-eligible target for a model override.
    /// </summary>
    IReviewerModelResolver? ReviewerModelResolver => null;

    /// <summary>
    /// Per-vendor preparation BEFORE the PTY is spawned. Implementations:
    ///   • Overlay vendor-specific settings dir from source repo into worktree
    ///   • Pre-trust the worktree path in the vendor's config file
    ///   • Write any vendor-specific config (MCP, etc.)
    ///   • Merge dialog-selected tools into vendor-specific permission shape
    ///   • Run fail-fast preflight checks (e.g. required CLI hooks installed)
    /// Two failure modes are supported and the orchestrator distinguishes them:
    ///   • Filesystem / parse errors are swallowed inside the launcher with a
    ///     warning log so a settings-overlay glitch never blocks launch.
    ///   • Typed preflight exceptions (e.g. CodexHooksNotInstalledException)
    ///     propagate out and the orchestrator converts them into LaunchFailed
    ///     with the exception's user-facing message. Use these sparingly.
    /// </summary>
    void Prepare(LauncherContext ctx);

    /// <summary>Build the argv array passed to the CLI from structured server fields.</summary>
    LaunchArgs BuildArgs(LauncherContext ctx);

    /// <summary>
    /// Build argv for a local <c>agent start</c> launch: emit only the mandatory daemon-level
    /// flags this vendor must always set, then append the user's verbatim post-<c>--</c>
    /// args. Used by the local-attach path instead of <see cref="BuildArgs"/>.
    /// </summary>
    LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs);

    /// <summary>Per-vendor cleanup AFTER the agent exits / is stopped.</summary>
    void Cleanup(AgentInstance agent);
}

internal sealed record LauncherContext(
        string                            AgentId,
        string                            SourceRepoPath,
        WorktreeInfo                      Worktree,
        string?                           Prompt,
        // Nullable for the same reason as RuntimeStartContext.Model, which feeds it: a launch whose
        // runtime cannot apply a model carries none. Both PTY launchers already handle absence —
        // ClaudeLauncher via string.IsNullOrEmpty, CodexLauncher via AddModelArg's own null guard — so
        // this makes the type match what they were already written to expect.
        string?                           Model,
        string?                           Effort,
        string[]?                         Tools,
        bool                              IsReview,
        bool                              IsReviewFlow,
        ReviewLaunchInfo?                 Review,
        ReviewLaunchBuilder.ReviewLaunch? ReviewLaunch
    ) {
    /// <summary>Owned worktree (daemon-created) vs borrowed cwd (the user's own checkout).
    /// Borrowed-cwd launches skip repo-mutating <c>Prepare()</c> steps.</summary>
    public WorkLocation Work { get; init; } = WorkLocation.OwnedWorktree;

    /// <summary> D-c: the review-flow definition's MCP allowlist, carried verbatim from
    /// <see cref="Capacitor.Cli.Core.LaunchAgentCommand.McpAllowlist"/>. Launchers resolve each
    /// name against the kcap-owned MCP registry and materialize matching servers into the vendor's
    /// MCP config, stripping any flow-starting server regardless of listing. Null/local-spawn
    /// launches (e.g. <c>kcap agent start</c>) never set this.</summary>
    public string[]? McpAllowlist { get; init; }

    /// <summary>Caller-selected Codex sandbox/approval posture. Non-null only for an interactive
    /// daemon-owned-worktree launch whose block already passed the orchestrator's
    /// <c>CodexPosturePolicy</c> guard; null for every other launch, which keeps the derived
    /// containment values.</summary>
    public CodexLaunchPosture? CodexPosture { get; init; }

    /// <summary>Caller-selected Claude permission mode, forwarded verbatim as
    /// <c>--permission-mode</c>. Non-null only for an interactive daemon-owned-worktree Claude launch
    /// that passed the orchestrator's <c>ClaudePermissionModePolicy</c> guard.</summary>
    public string? PermissionMode { get; init; }
}

internal readonly record struct LaunchArgs(string[] Args, string? McpConfigPath);
