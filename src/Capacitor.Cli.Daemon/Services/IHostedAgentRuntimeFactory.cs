using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A runtime's unattended-hosting advertisement: whether it is offered, and — when a daemon-local
/// gate is what withholds it — the operator-actionable reason.
/// </summary>
/// <param name="Supported">What <see cref="IHostedAgentRuntimeFactory.SupportsUnattended"/> reports.</param>
/// <param name="WithheldReason">Non-null only when the vendor CAN host unattended agents and this
/// daemon is refusing to offer it. Null both when the vendor is offered and when it never claimed
/// unattended support in the first place.</param>
internal readonly record struct UnattendedSupport(bool Supported, string? WithheldReason);

/// <summary>
/// Runtime-selection seam: one implementation per vendor family, chosen by
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> via <c>cmd.Vendor</c> instead of the orchestrator
/// itself building the vendor-specific runtime inline. <see cref="PtyHostedAgentRuntimeFactory"/>
/// wraps an <see cref="IHostedAgentLauncher"/> + <see cref="Pty.IPtyProcessFactory"/> for the
/// interactive CLIs (Claude, Codex); <see cref="AcpHostedAgentRuntimeFactory"/> spawns
/// <c>cursor-agent acp</c> and speaks ACP JSON-RPC for Cursor.
/// </summary>
internal interface IHostedAgentRuntimeFactory {
    /// <summary>Vendor token this factory handles ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>
    /// Reports whether this vendor's CLI resolves to an executable that looks installed. Used at
    /// daemon startup to build the vendor list advertised over <c>DaemonConnect</c>.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Whether this vendor can host a fully UNATTENDED agent (<see cref="LaunchKind.ReviewFlow"/>).
    /// The orchestrator refuses an unattended launch for a vendor that returns <c>false</c>.
    /// </summary>
    bool SupportsUnattended { get; }

    /// <summary>
    /// <see cref="SupportsUnattended"/> and the reason it is withheld, in ONE evaluation — the gated
    /// reviewers spawn their vendor binary to decide, so two calls would probe it twice per startup.
    ///
    /// <para><see cref="UnattendedSupport.WithheldReason"/> is populated ONLY for a vendor THIS daemon's
    /// configuration is refusing, never for one that simply does not offer unattended hosting. That
    /// asymmetry is what makes the reason worth surfacing to an operator.</para>
    /// </summary>
    UnattendedSupport DescribeUnattendedSupport() => new(SupportsUnattended, null);

    /// <summary>Whether this runtime has a certified containment strategy for review flows that
    /// request the caller's current checkout contents.</summary>
    bool SupportsBorrowedReviewFlow => false;

    /// <summary>Stable protocol token naming the certified containment boundary.</summary>
    string? BorrowedReviewContainment => null;

    /// <summary>Whether a borrowed request must be materialized into an independent daemon-owned snapshot
    /// before this runtime is started.</summary>
    bool BorrowedReviewRequiresIndependentSnapshot => false;

    /// <summary>
    /// Whether a review-flow launch this runtime serves runs the vendor — and therefore every MCP
    /// child the vendor spawns — under a <c>HOME</c> that is not the daemon user's.
    ///
    /// <para>That is a delivery fact, not a containment one. The <c>kcap-flow-result</c> channel
    /// resolves its credential from <see cref="Core.PathHelpers"/>' config dir, which hangs off
    /// <c>HOME</c>, so a redirected launch's channel reads an empty directory and cannot authenticate.
    /// Such a launch must be given the daemon-brokered delivery capability instead of
    /// <c>KCAP_URL</c> — see <c>RuntimeStartContext.RequiresBrokeredResultDelivery</c>.</para>
    ///
    /// <para><b>Deliberately independent of borrowed-ness.</b> A borrowed snapshot is one CAUSE of a
    /// redirected home (the Copilot sandbox moves it into a per-launch state dir), not the property
    /// itself: Antigravity isolates <c>HOME</c> on every launch it serves and refuses a borrowed
    /// workspace outright. Keying delivery on borrowed-ness alone therefore left that reviewer on the
    /// path it cannot use — it produced a correct answer and then failed to submit it.</para>
    ///
    /// <para>Not a vendor test, and not an advertisement gate: a runtime that answers <c>true</c> is
    /// simply promising that its review launches need brokered delivery, and the orchestrator mints
    /// accordingly.</para>
    /// </summary>
    bool ReviewFlowRedirectsHome => false;

    /// <summary>
    /// This runtime's reviewer MODEL override resolver, or <see langword="null"/> when the vendor has
    /// no authoritative resolver yet. PTY factories delegate to their launcher-owned policy; ACP /
    /// multi-provider factories return <see langword="null"/> (which does not remove their existing
    /// vendor-only unattended support). Consumed at startup to advertise
    /// <c>UnattendedVendorCapability.SupportsReviewerModelResolution</c>.
    /// </summary>
    IReviewerModelResolver? ReviewerModelResolver => null;

    /// <summary>
    /// Whether this runtime can actually APPLY a caller-supplied model. When <see langword="false"/>, a
    /// requested model is discarded by the runtime, so the orchestrator must not report it as the model
    /// the process is running — see <see cref="ModelSelectionLaunchPolicy"/>.
    ///
    /// <para>Defaults to <see langword="true"/> because every runtime that existed before this seam
    /// does honour a model: PTY launchers pass one on argv, and both ACP vendors carried a real
    /// selector. A <see langword="false"/> value is the new case, introduced by a vendor whose
    /// model-selection hook is unverified.</para>
    /// </summary>
    bool SupportsModelSelection => true;

    /// <summary>
    /// Prepares and starts the hosted runtime for this launch. Throws
    /// <see cref="CodexHooksNotInstalledException"/> for the orchestrator to map to a
    /// <c>LaunchFailed</c> with worktree cleanup; any other exception is likewise mapped to
    /// <c>LaunchFailed</c> (failed-launch path). Returns the started runtime plus any temp
    /// mcp-config path the orchestrator must record on <see cref="AgentInstance"/> for cleanup.
    /// </summary>
    Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct);
}

/// <summary>Result of <see cref="IHostedAgentRuntimeFactory.StartAsync"/>: the started runtime and
/// any temp mcp-config path the orchestrator must record on <see cref="AgentInstance.McpConfigPath"/>
/// so it's cleaned up alongside the agent (PTY launchers only — the ACP factory returns null).
///
/// <b>Concrete bind-handoff:</b> <paramref name="Transcript"/>
/// (renamed record — see below) exposes the ACP session metadata + aggregated transcript
/// (<see cref="IAcpTranscriptSource"/>) the orchestrator needs to bind (<c>AcpSessionStarted</c>) and
/// forward (<c>AcpSessionEvents</c>), without downcasting <see cref="Runtime"/> or re-deriving state
/// the runtime already resolved. <see cref="AcpHostedAgentRuntimeFactory"/> sets it to the runtime it
/// builds (which implements <see cref="IAcpTranscriptSource"/> directly); every PTY factory
/// (<see cref="PtyHostedAgentRuntimeFactory"/>) leaves it at its default <see langword="null"/> — no
/// PTY-side code needs to change for this field to exist.</summary>
internal readonly record struct HostedRuntimeStart(IHostedAgentRuntime Runtime, string? McpConfigPath, IAcpTranscriptSource? Transcript = null);

/// <summary>
/// Everything a runtime factory needs to prepare and start a hosted agent for one launch. Built by
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> from the inbound <c>LaunchAgentCommand</c> plus
/// daemon-local state (worktree, review-launch info, permission-bridge URL) after the pre-flight
/// guards (vendor known, unattended support, repo allowed, worktree created) have already passed.
/// </summary>
/// <param name="CapacitorPath">
/// Absolute path to the <c>kcap</c> binary (<c>DaemonConfig.CapacitorPath</c>) — passed to
/// <see cref="ReviewLaunchBuilder.BuildAsync"/> as the review-launch MCP server command. This is
/// deliberately NOT the vendor CLI path (<c>launcher.CliPath</c>): the review agent must run
/// <c>kcap mcp review</c>, since inside the daemon the running process is <c>kcap-daemon</c> with
/// no <c>mcp review</c> subcommand of its own, and <c>claude</c>/<c>codex</c> have no such
/// subcommand at all (PR #244 review, Fix A — a post-refactor regression had passed
/// <c>launcher.CliPath</c> here instead).
/// </param>
internal sealed record RuntimeStartContext(
        string            AgentId,
        string            Vendor,
        string            SourceRepoPath,
        WorktreeInfo      Worktree,
        string?           Prompt,
        // Nullable: a launch whose runtime cannot APPLY a model carries none, rather than carrying one
        // the process will not run (see ModelSelectionLaunchPolicy). Every launcher already treats an
        // absent model as "use the vendor default" — Claude and the ACP factory via
        // string.IsNullOrEmpty, Codex via AddModelArg — so null is the value they are prepared for.
        string?           Model,
        string?           Effort,
        string[]?         Tools,
        bool              IsReview,
        bool              IsReviewFlow,
        ReviewLaunchInfo? Review,
        ushort            Cols,
        ushort            Rows,
        string?           ServerUrl,
        string?           DaemonBridgeUrl,
        string            CapacitorPath,
        // D-c: the review-flow definition's MCP allowlist, carried verbatim from
        // LaunchAgentCommand.McpAllowlist. PTY launchers materialize it into a temp mcp-config;
        // the ACP factory resolves it (TryResolveReviewFlowAllowlist) into extra session/new
        // servers, admitted by an aliasing vendor's name gate under per-launch wire names.
        string[]?         McpAllowlist = null,
        // Phase A: owned worktree (daemon-created) vs borrowed cwd (the user's own
        // checkout), carried from LaunchAgentCommand.Borrowed through to LauncherContext.Work.
        // Defaults to OwnedWorktree — today's only exercised path — unchanged.
        WorkLocation       Work = WorkLocation.OwnedWorktree,
        // Phase B (D4 §6.4(3)): daemon-identity env markers stamped into the spawned child so a
        // RESTARTED daemon's OrphanReaper env-marker scan can recognize a recordless survivor as its
        // own (KCAP_DAEMON_ID == this daemon) from a PRIOR incarnation (KCAP_DAEMON_EPOCH != current)
        // and reap it. Empty when a test/legacy caller omits them — the markers are simply not written.
        string             DaemonId    = "",
        string             DaemonEpoch = "",
        // Optional MCP-server list for session/new — null/empty for every launch today
        // (no caller populates this yet; interactive launches must keep it empty). The reviewer
        // path is the first planned consumer. Distinct from McpAllowlist above, which is a PTY-only
        // materialization concern (an allowlist of names the launcher writes into a temp
        // mcp-config file) — this is the literal ACP session/new payload for descriptors that
        // support it.
        IReadOnlyList<AcpMcpServerSpec>? McpServers = null,
        // True only when a borrowed request has been materialized into a fully independent,
        // daemon-owned repository snapshot. Factories use this to revalidate exact artifacts.
        bool               IsBorrowedSnapshot = false,
        // Exact loopback GET capability for the immutable Git-index review-context generation.
        // Present only for borrowed-snapshot review flows; never a backend or filesystem URL.
        string?            ReviewContextCapabilityUrl = null,
        // Exact loopback POST capability the result channel submits through, so a reviewer whose HOME
        // is not the daemon user's can report without a credential of its own. The daemon holds the
        // authenticated connection and forwards; nothing here is a backend URL.
        string?            FlowResultCapabilityUrl = null,
        // Whether this launch's result channel MUST deliver through FlowResultCapabilityUrl rather
        // than authenticating for itself, in which case the capability is required — not merely
        // permitted — and its absence fails the launch instead of falling back to KCAP_URL.
        //
        // Derived by the orchestrator, because neither party alone determines it: a borrowed snapshot
        // is one cause (the sandbox redirects HOME) and IHostedAgentRuntimeFactory
        // .ReviewFlowRedirectsHome is the other. The ONE thing AcpReviewFlowMcp reads, so the two
        // causes cannot end up expressed differently at the two ends of the same launch.
        bool               RequiresBrokeredResultDelivery = false,
        // Caller-selected Codex sandbox/approval posture, carried verbatim from
        // LaunchAgentCommand.CodexPosture through to LauncherContext.CodexPosture. Non-null only for
        // an interactive daemon-owned-worktree Codex launch that passed the orchestrator's guard.
        CodexLaunchPosture? CodexPosture = null,
        // The per-launch generated names for THIS launch (result-channel wire name, unmatchable MCP name).
        //
        // NOT a caller input, despite living on the context: the factory overwrites it at the top of
        // StartAsync, so a value supplied on the way in never reaches a launch (asserted by test). It sits
        // here so one instance reaches both the session/new MCP list and the argv builder through the
        // existing connectionSource seam — which is the whole point, since two independent derivations of
        // these names is the defect LaunchIdentity exists to make unrepresentable.
        //
        // Null on any path that has not been through the factory, and on the PTY launchers, which have no
        // MCP-name gate to defend.
        LaunchIdentity?     LaunchIdentity = null,
        // The SAME per-launch clock the orchestrator threads onto the eventual AgentInstance and the
        // reviewer permission-bridge grant. Handed to the factory so an ACP factory can wire it onto
        // its runtime BEFORE StartAsync: assigned any later, every SetLaunchStage inside
        // AcpHostedAgentRuntime.StartAsync is a silent no-op against a null clock. Null for the PTY
        // launchers (AgentInstance owns their clock) and for constructions predating this field.
        AgentActivityClock? ActivityClock = null,
        // Caller-selected ACP permission preset ("explore"/"edit"), carried verbatim from
        // LaunchAgentCommand.AcpPermissionPreset. The ACP factory resolves it (for non-review-flow
        // launches only) into an AcpLaunchPermissionPreset and wires it onto the interaction bridge.
        // Null for every non-preset launch and constructions predating this field.
        string?             AcpPermissionPreset = null,
        // The hosted Codex thread to RESUME, carried verbatim from LaunchAgentCommand.ResumeSessionId.
        // Non-null only for a parked reviewer relaunch: the Codex app-server runtime reopens this thread
        // via thread/resume instead of thread/start, and suppresses the second SessionStarted. Null for
        // a fresh launch, every non-Codex/non-app-server launch, and constructions predating this field.
        string?             ResumeSessionId = null
    );
