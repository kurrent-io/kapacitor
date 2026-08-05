using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>How an unattended ACP reviewer receives its validated stdio MCP servers.</summary>
internal enum AcpReviewFlowMcpTransport {
    /// <summary>Infer the transport from <see cref="AcpVendorDescriptor.SupportsMcpServers"/>.</summary>
    Default,
    /// <summary>The vendor cannot carry the reviewer's required result channel.</summary>
    Unsupported,
    /// <summary>Send the servers in ACP <c>session/new.mcpServers</c>.</summary>
    SessionNew,
    /// <summary>Preload the servers through Copilot CLI's <c>--additional-mcp-config</c>.</summary>
    CopilotAdditionalConfig
}

/// <summary>How an ACP vendor handles a server→client interaction frame during an unattended
/// review. <see cref="AutoApprove"/> preserves the existing Copilot behavior. <see cref="Fail"/>
/// is the stronger Cursor contract: Cursor's own launch flags must suppress interaction frames,
/// so receiving even one means that contract has regressed and the reviewer is reaped.</summary>
internal enum AcpUnattendedInteractionPolicy {
    Disabled,
    AutoApprove,
    Fail
}

/// <summary>The security boundary used to serve borrowed-checkout context without exposing the
/// caller's live checkout to an unattended reviewer.
///
/// <para><b>These are not interchangeable labels, and the difference bit this feature once.</b> The
/// token is reported to the server, which routes on it, so what each one PROMISES has to be stated
/// rather than inferred from the name:</para>
///
/// <list type="bullet">
/// <item><b>NativeToolClamp</b> — the reviewer runs directly in the caller's borrowed checkout and
/// the vendor's own launch flags are the only thing keeping it from writing there. Containment and
/// readability are coupled: the same clamp that blocks writing is what blocks reading. A vendor whose
/// clamp is an OS sandbox keeps its read tools; a vendor whose clamp is a tool allowlist may end up
/// with no tools at all, which is a reviewer that cannot see the code it was asked to review.</item>
/// <item><b>IndependentSnapshot</b> — the daemon materializes the authorized contents into its own
/// directory and the reviewer never touches the caller's checkout at all. Containment comes from the
/// filesystem boundary, so the tool surface is free to be READABLE. This is the token that permits a
/// read-capable launch, and it is why <see cref="CopilotBorrowedReviewPolicy"/> exists: Copilot is
/// only borrowed-capable on a platform where its snapshot tool surface has been verified.</item>
/// </list>
///
/// <para>Consequence worth stating plainly: a snapshot-materialized launch handed to a vendor
/// declaring anything but <see cref="IndependentSnapshot"/> is a wiring bug, and is rejected before
/// spawn.</para></summary>
internal enum AcpBorrowedReviewContainment {
    None,
    NativeToolClamp,
    IndependentSnapshot
}

/// <summary>
/// Per-vendor wiring for AcpHostedAgentRuntimeFactory: which binary to spawn, what argv an
/// interactive vs. an unattended (review-flow) launch gets, and how (or whether) this vendor's ACP
/// surface supports model selection / an mcpServers list. Cursor and Copilot are registered today;
/// onboarding another ACP-speaking vendor means adding one descriptor + a factory registration
/// line, not touching AcpHostedAgentRuntimeFactory itself.
///
/// <b>Round 2 Finding 2 — the selector object is the only source of truth:</b> there is
/// deliberately no separate "does this vendor support model selection" boolean.
/// <see cref="ModelSelector"/> alone decides: a vendor that doesn't select models carries
/// <see cref="NoOpModelSelector.Instance"/>; Cursor carries
/// <see cref="ConfigOptionModelSelector.Instance"/>. An earlier revision shipped a
/// <c>SupportsModelSelection</c> flag plus a construction-time invariant rejecting a
/// <c>false</c>-flagged descriptor that still carried a real selector — but by the time that
/// invariant existed, <see cref="Services.AcpHostedAgentRuntimeFactory.StartAsync"/> (D4) already forwarded
/// <see cref="ModelSelector"/> unconditionally and never branched on the flag, so the flag gated no
/// behavior; worse, the invariant only checked ONE of its two possible contradictions (it rejected
/// <c>SupportsModelSelection: false</c> + a real selector, but still accepted the equally
/// contradictory <c>SupportsModelSelection: true</c> + <c>NoOpModelSelector.Instance</c>). Removing
/// the field removes the dead state and the asymmetric guard together: there is exactly one thing
/// to get right — which selector object the descriptor carries — not a boolean that also has to
/// agree with it. This also drops any expectation that <c>ModelSelector</c> be
/// <c>ReferenceEquals</c> to one of the two singletons below: the runtime only needs SOME
/// <see cref="IAcpModelSelector"/>, so a future vendor's own implementation, or a test double, is
/// exactly as valid.
///
/// <c>Argv</c>/<c>UnattendedTrustArgv</c> are <see cref="ImmutableArray{T}"/>, not <c>string[]</c>:
/// a <c>static readonly</c> descriptor singleton (see <see cref="AcpVendorDescriptors"/>), shared
/// across every launch for the daemon's whole lifetime, must not expose a mutable array a caller
/// could reach in and alter, silently corrupting every later (and any concurrent) launch.
///
/// <b>Task-review defensive hardening:</b> this record uses an EXPLICIT constructor — not a
/// positional-record primary constructor, which can't carry a normalizing body — for the same
/// reason as <see cref="Core.Acp.AcpMcpServerSpec"/>: <c>default(ImmutableArray{string})</c> (the
/// unallocated sentinel, distinct from <see cref="ImmutableArray{T}.Empty"/>) throws a
/// <see cref="NullReferenceException"/> on enumeration, not on construction — so a descriptor built
/// by a future author who forgets to pass <c>[]</c>/an initializer for <see cref="Argv"/> or
/// <see cref="UnattendedTrustArgv"/> would construct successfully and only blow up later, on first
/// use, far from the mistake. The constructor normalizes both to
/// <see cref="ImmutableArray{T}.Empty"/> up front so that class of bug can't happen at all. Every
/// call site today already passes <c>[]</c>/<c>["acp"]</c> explicitly, so this is purely
/// defensive — no observable behavior change.
/// </summary>
internal sealed record AcpVendorDescriptor {
    public string                      Vendor                 { get; }
    public Func<DaemonConfig, string>  ResolveBinaryPath     { get; }
    public Func<DaemonConfig, string?> ResolveDefaultModel   { get; }
    public ImmutableArray<string>      Argv                   { get; }
    public ImmutableArray<string>      UnattendedTrustArgv    { get; }
    public bool                        SupportsUnattended     { get; }
    public AcpUnattendedInteractionPolicy UnattendedInteractionPolicy { get; }
    public IAcpModelSelector           ModelSelector          { get; }
    public bool                        SupportsMcpServers     { get; }
    public AcpReviewFlowMcpTransport   ReviewFlowMcpTransport { get; }
    public bool                        SupportsBorrowedReviewFlow { get; }
    public AcpBorrowedReviewContainment BorrowedReviewContainment { get; }

    /// <summary>
    /// Whether an INTERACTIVE hosted session of this vendor may attempt crash reconnect/resume
    /// (relaunch → <c>initialize</c> → <c>session/load</c>, replay suppressed). This is a
    /// PROBE-VERIFIED fact, never inferred from the vendor's advertised <c>loadSession</c>
    /// capability — all four registered vendors advertise it, and two measurably cannot honor it
    /// across a crashed owner (<c>docs/probes/2026-08-04-acp-reconnect-c0/</c>): Kiro refuses with a
    /// durable stale-owner lock ("Session is active in another process", identical at 0/15/60s), and
    /// Gemini never persists a crash-killed session ("No previous sessions found"). Flipping a
    /// vendor to <see langword="true"/> requires a passing re-run of that probe, because the resume
    /// path additionally relies on the vendor honoring the ACP session/load response-after-replay
    /// barrier — a violation would duplicate transcript content. Runtime eligibility is conjunctive
    /// with the handshake's actual <c>loadSession</c> advertisement, an interactive (non-review)
    /// launch, and the <c>KCAP_ACP_RECONNECT</c> kill switch.
    /// </summary>
    public bool                        SupportsReconnectResume { get; }

    public AcpVendorDescriptor(
            string                      Vendor,
            Func<DaemonConfig, string>  ResolveBinaryPath,
            Func<DaemonConfig, string?> ResolveDefaultModel,
            ImmutableArray<string>      Argv,
            ImmutableArray<string>      UnattendedTrustArgv,
            bool                        SupportsUnattended,
            IAcpModelSelector           ModelSelector,
            bool                        SupportsMcpServers,
            AcpReviewFlowMcpTransport   ReviewFlowMcpTransport = AcpReviewFlowMcpTransport.Default,
            bool                        SupportsBorrowedReviewFlow = false,
            AcpUnattendedInteractionPolicy UnattendedInteractionPolicy = AcpUnattendedInteractionPolicy.Disabled,
            AcpBorrowedReviewContainment BorrowedReviewContainment = AcpBorrowedReviewContainment.None,
            bool                        SupportsReconnectResume = false
        ) {
        var normalizedUnattendedTrustArgv = UnattendedTrustArgv.IsDefault ? ImmutableArray<string>.Empty : UnattendedTrustArgv;

        if (!SupportsUnattended && !normalizedUnattendedTrustArgv.IsEmpty)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgv)} must be empty when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedTrustArgv));

        if (SupportsBorrowedReviewFlow && !SupportsUnattended)
            throw new ArgumentException(
                $"{nameof(SupportsBorrowedReviewFlow)} requires {nameof(SupportsUnattended)} (vendor: {Vendor}).",
                nameof(SupportsBorrowedReviewFlow));

        if (SupportsBorrowedReviewFlow && BorrowedReviewContainment == AcpBorrowedReviewContainment.None)
            throw new ArgumentException(
                $"{nameof(SupportsBorrowedReviewFlow)} requires an explicit borrowed-checkout containment boundary (vendor: {Vendor}).",
                nameof(BorrowedReviewContainment));

        if (!SupportsUnattended && UnattendedInteractionPolicy != AcpUnattendedInteractionPolicy.Disabled)
            throw new ArgumentException(
                $"{nameof(UnattendedInteractionPolicy)} must be Disabled when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedInteractionPolicy));

        if (SupportsUnattended && UnattendedInteractionPolicy == AcpUnattendedInteractionPolicy.Disabled)
            throw new ArgumentException(
                $"{nameof(UnattendedInteractionPolicy)} must be explicit when {nameof(SupportsUnattended)} is true (vendor: {Vendor}).",
                nameof(UnattendedInteractionPolicy));

        this.Vendor              = Vendor;
        this.ResolveBinaryPath   = ResolveBinaryPath;
        this.ResolveDefaultModel = ResolveDefaultModel;
        this.Argv                = Argv.IsDefault ? ImmutableArray<string>.Empty : Argv;
        this.UnattendedTrustArgv = normalizedUnattendedTrustArgv;
        this.SupportsUnattended  = SupportsUnattended;
        this.UnattendedInteractionPolicy = UnattendedInteractionPolicy;
        this.ModelSelector       = ModelSelector;
        this.SupportsMcpServers  = SupportsMcpServers;
        this.SupportsBorrowedReviewFlow = SupportsBorrowedReviewFlow;
        this.BorrowedReviewContainment = BorrowedReviewContainment;
        this.SupportsReconnectResume = SupportsReconnectResume;
        this.ReviewFlowMcpTransport = ReviewFlowMcpTransport switch {
            AcpReviewFlowMcpTransport.Default when SupportsMcpServers => AcpReviewFlowMcpTransport.SessionNew,
            AcpReviewFlowMcpTransport.Default                         => AcpReviewFlowMcpTransport.Unsupported,
            _                                                         => ReviewFlowMcpTransport
        };

        if (this.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.SessionNew && !SupportsMcpServers)
            throw new ArgumentException(
                $"{nameof(AcpReviewFlowMcpTransport.SessionNew)} requires {nameof(SupportsMcpServers)} (vendor: {Vendor}).",
                nameof(ReviewFlowMcpTransport));
    }
}

internal static class AcpVendorDescriptors {
    /// <summary>Cursor CLI's ACP hosted-agent surface. Review flows use Cursor's own unattended
    /// controls: <c>--force</c> suppresses command approval unless explicitly denied,
    /// <c>--approve-mcps</c> suppresses MCP-server approval, and <c>--trust</c> suppresses workspace
    /// trust. kcap does not auto-approve a fallback frame: any permission, elicitation, or unknown
    /// interaction request is a contract violation and reaps the reviewer.</summary>
    public static readonly AcpVendorDescriptor Cursor = new(
        Vendor:              "cursor",
        ResolveBinaryPath:   cfg => cfg.CursorPath,
        ResolveDefaultModel: cfg => cfg.CursorModel,
        Argv:                ["acp"],
        UnattendedTrustArgv: ["--force", "--approve-mcps", "--trust"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        SupportsBorrowedReviewFlow: true,
        BorrowedReviewContainment: AcpBorrowedReviewContainment.IndependentSnapshot,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        // Probe-verified 2026-08-04 (docs/probes/2026-08-04-acp-reconnect-c0/): session/load works
        // across a SIGKILLed owner, the response-after-replay barrier holds, and a loaded session
        // prompts normally. Note the same probe found Cursor's replay REWRITES toolCallIds and
        // drops the interrupted turn — both irrelevant to the suppress-the-replay resume, but they
        // are why per-envelope replay matching stays impossible for this vendor.
        SupportsReconnectResume: true
    );

    /// <summary>GitHub Copilot CLI as an ACP hosted agent (<c>copilot --acp --stdio</c>).
    ///
    /// <para><b><see cref="AcpVendorDescriptor.SupportsMcpServers"/> is <c>false</c> on call-level
    /// measurement, not on the <c>mcpCapabilities</c> advertisement</b> — the advertised
    /// <c>{http, sse}</c> shape cannot decide this flag either way (Kiro and Gemini advertise exactly
    /// the same shape and both honour stdio servers). Measured on macOS against Copilot CLI 1.0.78
    /// (2026-08-04): a purpose-built stdio server passed in <c>session/new.mcpServers</c> is silently
    /// ignored. <c>session/new</c> succeeds, but the server process is never spawned (its own log
    /// stays empty), no tool-call frame ever references it, and the model reports the tool
    /// unavailable — identical on the interactive argv and on the full unattended review argv, where
    /// <c>--available-tools</c> additionally rejects the injected tool's flattened id as an unknown
    /// tool name. The same server, same build, same driver preloaded through
    /// <c>--additional-mcp-config</c> completes <c>initialize</c> → <c>tools/list</c> →
    /// <c>tools/call</c> with the tool's nonce reaching the model and the turn ending
    /// <c>end_turn</c> — so the negative is Copilot's <c>session/new</c> handling, not the probe.
    /// Re-flip only on an equivalent call-level probe succeeding against a newer build.</para>
    ///
    /// <para>Review flows therefore preload their validated stdio servers through Copilot's
    /// <c>--additional-mcp-config</c> process argument and clamp the visible tool surface.</para></summary>
    public static readonly AcpVendorDescriptor Copilot = new(
        Vendor:              "copilot",
        ResolveBinaryPath:   cfg => cfg.CopilotPath,
        ResolveDefaultModel: _ => null,
        Argv:                ["--acp", "--stdio"],
        UnattendedTrustArgv: ["--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  false,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.CopilotAdditionalConfig,
        // Borrowed review is DELIBERATELY not declared here. Copilot's borrowed capability is
        // platform-resolved by CopilotBorrowedReviewPolicy, because the tool surface that makes a
        // borrowed snapshot both readable and contained has only been verified on some platforms.
        // Leaving a static declaration would put a second, always-stale answer in the codebase; a
        // reader who consulted it would get a containment token that no longer describes the launch.
        // If the platform special case were ever dropped, this default disables borrowed review
        // rather than silently permitting an unverified surface — the safe direction to fail.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove,
        // Probe-verified 2026-08-04 (docs/probes/2026-08-04-acp-reconnect-c0/): session/load works
        // across a SIGKILLed owner, the barrier holds, toolCallIds are stable, and — unlike
        // Cursor — Copilot PERSISTS a mid-turn-killed prompt agent-side, which is why the
        // interrupted-turn disposition keys on local send facts, never on replay content.
        SupportsReconnectResume: true
    );

    /// <summary>AWS Kiro CLI as an ACP hosted agent (<c>kiro-cli acp</c>). Interactive hosting only:
    /// unattended review is deliberately withheld until its own issue lands the containment mechanism
    /// (Kiro inherits the user's GLOBAL <c>~/.kiro/settings/mcp.json</c> servers into every ACP
    /// session, so an unattended reviewer would be handed <c>kcap-flows</c> and could start nested
    /// flows). Interactive hosting is unaffected by that inheritance — it is the desired behavior
    /// there.
    ///
    /// <para><b><see cref="SupportsMcpServers"/> is <c>true</c> here while <see cref="Copilot"/> sets
    /// it <c>false</c>, and the reasoning is NOT contradictory.</b> Both vendors advertise the same
    /// ACP <c>mcpCapabilities</c> shape (<c>{http, sse}</c> — no stdio), so the advertisement cannot be
    /// what decides it. Copilot's <c>false</c> is an empirical finding about Copilot. Kiro was probed
    /// directly: a purpose-built stdio server passed in <c>session/new.mcpServers</c> was driven all
    /// the way to a real <c>tools/call</c>, with the tool's nonce reaching the model and the turn
    /// ending <c>end_turn</c>. Kiro honours stdio despite not advertising it.</para>
    ///
    /// <para>Note what that probe deliberately established, because a weaker signal was available and
    /// would have been wrong: <c>_kiro.dev/mcp/server_initialized</c> proves only that a server
    /// STARTED, not that its tools are discoverable or callable — a tool can be absent from
    /// <c>tools/list</c>, refused by trust policy, mis-namespaced, or fail at invocation. Flipping
    /// Copilot's flag needs an equivalent call-level probe against Copilot, not this result.</para>
    ///
    /// <para><see cref="SetModelSelector"/>, not <see cref="ConfigOptionModelSelector"/> and no
    /// longer <see cref="NoOpModelSelector"/> — measured, both halves
    /// (<c>docs/probes/2026-08-05-kiro-model-override/</c>, kiro-cli 2.16.0). The hosting work had
    /// deferred model override because <c>session/set_config_option</c>'s write half was unverified
    /// and the selector fails SILENTLY; the probe settled it in a direction the deferral did not
    /// anticipate: <c>session/set_config_option</c> does not exist on Kiro at all (<c>-32601 Method
    /// not found</c>), while the stabilized <c>session/set_model</c> both succeeds and TAKES EFFECT
    /// at the turn level — the next turn's backend inference request carried the requested
    /// <c>modelId</c> verbatim, the model self-identified as it (a different vendor family from the
    /// account's default, so not confusable), and Kiro's persisted session state recorded it with
    /// model-specific parameters (context window, rate multiplier). <c>ResolveDefaultModel</c> reads
    /// <c>DaemonConfig.KiroModel</c> (<c>KCAP_KIRO_MODEL</c>), which defaults to NULL — so a
    /// zero-configuration launch still runs Kiro's own default model and reports none, exactly the
    /// pre-override behaviour; a per-launch <c>RuntimeStartContext.Model</c> takes precedence as for
    /// Cursor.</para>
    ///
    /// <para><c>--agent-engine v1|v2|v3</c> (default <c>v2</c>) is deliberately NOT passed: pinning it
    /// diverges the hosted session from what the user gets interactively and buys an upgrade
    /// treadmill. Revisit only if a measured behavioural difference forces it.</para></summary>
    public static readonly AcpVendorDescriptor Kiro = new(
        Vendor:              "kiro",
        ResolveBinaryPath:   cfg => cfg.KiroPath,
        ResolveDefaultModel: cfg => cfg.KiroModel,
        Argv:                ["acp"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       SetModelSelector.Instance,
        SupportsMcpServers:  true,
        // Measured INELIGIBLE 2026-08-04 (docs/probes/2026-08-04-acp-reconnect-c0/): Kiro advertises
        // loadSession but refuses session/load after a SIGKILLed owner with a DURABLE stale-owner
        // lock — "Failed to start session: Session is active in another process (PID <dead>)",
        // byte-identical at 0s/+15s/+45s, so bounded-backoff retries cannot clear it. Flip only
        // after a vendor fix AND a passing probe re-run.
        SupportsReconnectResume: false
    );

    /// <summary>
    /// A placeholder the FACTORY replaces, per launch, with an unguessable name — so an MCP allowlist can
    /// deny everything without the deny-all value being a literal the repository can match.
    ///
    /// <para><b>A fixed sentinel was tried and is broken.</b> An earlier revision passed the literal
    /// <c>kcap-none</c> and called it "a name no MCP server will ever have". A contributor controls
    /// <c>.gemini/settings.json</c> and can simply name their server <c>kcap-none</c>; measured, that
    /// executes, and the clamp is bypassed completely. The comment asserted a property nothing enforced —
    /// a semantic label is not validation.</para>
    ///
    /// <para>The value must also be non-empty: <c>--allowed-mcp-server-names ""</c> fails Gemini's config
    /// load BEFORE the session starts, which is also a verification trap — the launch then fails for a
    /// reason unrelated to MCP and reports nothing loaded.</para>
    /// </summary>
    internal const string UnmatchableMcpNamePlaceholder = "__kcap_unmatchable_mcp_name__";

    /// <summary>Google Gemini CLI as an ACP hosted agent (<c>gemini --experimental-acp</c>).
    /// Hosted interactively and as an unattended review-flow reviewer.
    ///
    /// <para><b><c>--skip-trust</c> is required, and is NOT a containment measure.</b> Gemini refuses a
    /// headless turn in an untrusted directory outright — <c>exit 55</c> before any model call — and a
    /// daemon-created worktree cannot be assumed pre-trusted, so without the flag every launch fails.
    /// What it does NOT do is protect anything: trust INHERITS from a trusted parent directory, and a
    /// daemon worktree lives at <c>&lt;repo&gt;/.capacitor/worktrees/agent-…</c>, INSIDE the operator's
    /// repository. An operator who has trusted their own repo therefore gives every hosted Gemini agent
    /// inherited trust over the checked-out branch's configuration — and passing <c>--skip-trust</c> does
    /// not withdraw it. Measured, not assumed.</para>
    ///
    /// <para><b>Which is why the MCP allowlist is here.</b> Under inherited trust, a repository-authored
    /// <c>.gemini/settings.json</c> MCP server WAS observed starting on the ACP path — repo-controlled
    /// process execution under the daemon user, before the model acts, on a branch that may be
    /// contributor-authored. <see cref="UnmatchableMcpNamePlaceholder"/> — replaced per launch with an
    /// unguessable name — reduces the allowlist to nothing the repository can match, which blocks it. Repo-authored <i>hooks</i> were separately measured NOT to run on the ACP path (they do
    /// on the <c>--prompt</c> path — the two paths differ, and neither predicts the other).</para>
    ///
    /// <para><b>Deny-all is the launch default; a review launch opens the gate to exactly the servers it
    /// injects.</b> The factory replaces the substituted value with the comma-joined names of the built
    /// <c>session/new</c> list — the result channel plus any resolved allowlist servers (replace, never
    /// append — the option is comma-coerced, so a second option occurrence would widen the gate rather
    /// than move it). Every one of those names is a per-launch alias, because a canonical id is a fixed
    /// public literal the reviewed repository could declare its own server under and have it spawned as
    /// the daemon user (the impersonation shape measured in spec §2.3/§2.6; multi-name admission
    /// measured on 0.53.0 — both admitted servers reach <c>tools/call</c>, an injected name outside the
    /// gate never spawns). An interactive launch injects nothing and keeps the unguessable deny-all,
    /// which permits nothing and costs nothing; a future interactive caller populating
    /// <c>RuntimeStartContext.McpServers</c> must widen the gate in the same change.
    /// <c>AcpVendorDescriptorTests</c> and <c>GeminiReviewerLaunchTests</c> assert both halves, the
    /// latter pinning gate == injected set.</para>
    ///
    /// <para><see cref="NoOpModelSelector"/> because Gemini's model-selection WRITE half is
    /// unverified: <c>session/new</c> does return a <c>models</c> object, so a live selector's read
    /// half would fit, but both wire selectors fail SILENTLY when the write does not take — a
    /// session that reports the requested model while running another. <c>ResolveDefaultModel:
    /// null</c> alone is not enough, because <c>ResolveRequestedModel</c> prioritises a per-launch
    /// model and would reach a live selector anyway. Kiro's probe
    /// (<c>docs/probes/2026-08-05-kiro-model-override/</c>) is the template for flipping this: it
    /// found Kiro rejects <c>session/set_config_option</c> outright but honours
    /// <c>session/set_model</c> at effect level — Gemini needs its own equivalent effect-level
    /// measurement (which method, and does the turn actually run on it) before carrying
    /// <see cref="SetModelSelector"/> or <see cref="ConfigOptionModelSelector"/>.</para>
    ///
    /// <para><c>--approval-mode</c> is deliberately not passed: interactive hosting should behave as the
    /// user's own session does, and pinning <c>plan</c> would silently make hosted Gemini read-only.
    /// <c>DiagnosticBinary</c> needs no branch — the vendor key and the binary name are both
    /// <c>gemini</c>.</para></summary>
    public static readonly AcpVendorDescriptor Gemini = new(
        Vendor:              "gemini",
        ResolveBinaryPath:   cfg => cfg.GeminiPath,
        ResolveDefaultModel: _ => null,
        Argv:                ["--experimental-acp", "--skip-trust",
                              "--allowed-mcp-server-names", UnmatchableMcpNamePlaceholder],
        // --approval-mode yolo is REQUIRED for a reviewer, not a convenience: without it Gemini emits
        // session/request_permission before invoking its OWN injected result-channel tool, and no human is
        // there to answer, so the reviewer cannot report at all. Measured both ways (spec §2.4). Review
        // launches only — an interactive hosted session must behave as the user's own does (§3.3).
        UnattendedTrustArgv: ["--approval-mode", "yolo"],
        SupportsUnattended:  true,
        ModelSelector:       NoOpModelSelector.Instance,
        // Measured: Gemini DOES honour session/new.mcpServers — it spawns the stdio server, lists its tools
        // and calls them. This resolves the transport to SessionNew, which is what carries the reviewer's
        // result channel. the hosting work deferred this as a call-level probe; the answer is yes (spec §2.1).
        SupportsMcpServers:  true,
        // Fail rather than AutoApprove: with --approval-mode yolo, Gemini emits NO interaction frame at all,
        // so receiving one means the launch contract regressed (a dropped flag, a vendor change) and the
        // honest response is to reap the reviewer rather than auto-approve whatever it asked for (§3.4).
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        // Measured INELIGIBLE 2026-08-04 (docs/probes/2026-08-04-acp-reconnect-c0/): Gemini
        // advertises loadSession but a crash-killed session is never persisted — session/load
        // refuses with "No previous sessions found for this project", so there is nothing to
        // resume. Gemini also self-re-execs (a sandbox wrapper spawns an inner process with
        // identical argv), so "the spawned pid exited" and "the agent died" are different events;
        // any future enablement must re-probe BOTH persistence and process-tree semantics.
        SupportsReconnectResume: false
    );
}
