using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for ACP-speaking vendors, parameterized over an
/// <see cref="AcpVendorDescriptor"/> — spawns <c>{descriptor.ResolveBinaryPath(config)}
/// {descriptor.Argv}</c> as a child process, wraps its stdio in an <see cref="AcpConnection"/> +
/// <see cref="AcpChildProcess"/>, and drives the ACP handshake via
/// <see cref="AcpHostedAgentRuntime.StartAsync"/>. Cursor and Copilot descriptors share this path;
/// each descriptor declares its own unattended and MCP transport capabilities.
///
/// <b>Spec-review Finding 4:</b> gained a <see cref="ServerConnection"/> constructor
/// dependency so every runtime this factory produces has the real permission/elicitation bridge
/// wired — <see cref="StartAsync"/> passes <c>ctx.AgentId</c> and
/// <see cref="ServerConnection.RequestAcpInteractionAsync"/> into <see cref="AcpHostedAgentRuntime"/>'s
/// optional parameters instead of leaving them at their <c>""</c>/<see langword="null"/> defaults.
///
/// <b>Round-4 Finding 3:</b> process-spawning + stream construction is extracted into
/// <paramref name="connectionSource"/> (defaulting to <see cref="StartRealProcess"/>) purely so
/// <see cref="AcpHostedAgentRuntimeFactoryTests"/> can construct THIS class for real and drive its
/// REAL <see cref="StartAsync"/> against an in-memory <c>FakeAcpAgent</c> peer instead of a real
/// <c>cursor-agent acp</c> child process (unavailable and non-portable in CI) — the seam changes
/// nothing about production behavior, since the default IS the real `Process.Start`-backed path.
/// </summary>
internal sealed partial class AcpHostedAgentRuntimeFactory(
        AcpVendorDescriptor                                                            descriptor,
        DaemonConfig                                                                   config,
        ILoggerFactory                                                                 loggerFactory,
        ServerConnection                                                               connection,
        Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)>? connectionSource = null,
        // Test seam ONLY, for the certified-version decision. Production passes null, which interrogates the
        // real binary. Tests pin a value so the OPERATOR-FLAG half of the gate is assertable on a host with
        // no gemini installed — otherwise a disabled-daemon test passes for the wrong reason (unknown
        // version) and would keep passing if advertisement stopped honouring the flag.
        Func<string, string?>? resolveVendorVersion = null
    ) : IHostedAgentRuntimeFactory {
    readonly Func<string, string?>? _resolveVendorVersion = resolveVendorVersion;

    readonly Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)> _connectionSource =
        connectionSource ?? (ctx => StartRealProcess(descriptor, config, ctx, loggerFactory));

    readonly ILogger _logger = loggerFactory.CreateLogger<AcpHostedAgentRuntimeFactory>();

    /// <summary>Resolved ONCE for this daemon's platform. Every borrowed-review consumer below reads
    /// this, never the static descriptor — see <see cref="ResolvedBorrowedReviewPolicy"/> for why
    /// splitting them is how advertisement and spawn drift apart.</summary>
    internal static ResolvedBorrowedReviewPolicy PolicyFor(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.Copilot.Vendor
            ? CopilotBorrowedReviewPolicy.Current
            : ResolvedBorrowedReviewPolicy.FromDescriptor(descriptor);

    readonly ResolvedBorrowedReviewPolicy _policy = PolicyFor(descriptor);

    public string Vendor             => descriptor.Vendor;

    /// <summary>
    /// Whether this daemon advertises the vendor as unattended-capable. For an aliasing vendor (Gemini) this
    /// also requires the operator's capability gate AND a certified vendor version, so a daemon that has not
    /// opted in is never selected as a reviewer host in the first place.
    ///
    /// <para>Advertisement is an OPTIMISATION, not the boundary — the authoritative check is in
    /// <see cref="BuildProcessStartInfo"/>, immediately before the spawn, because an explicit
    /// <c>vendor: "gemini"</c> request can reach a launch without consulting advertisement.</para>
    /// </summary>
    public bool SupportsUnattended {
        get {
            if (!descriptor.SupportsUnattended)      return false;
            if (!AliasesResultChannel(descriptor))   return true;

            // Operator flag FIRST, and short-circuit. Review's point: evaluating the version probe as an
            // argument meant an installed-but-wedged vendor binary could hang daemon STARTUP even though the
            // reviewer was switched off — a hang on a code path the operator opted out of.
            if (!config.GeminiUnattendedReviewerEnabled) return false;

            return GeminiReviewerCapability.IsEnabled(
                true, (_resolveVendorVersion ?? ResolveGeminiVersion)(descriptor.ResolveBinaryPath(config)));
        }
    }
    public bool   SupportsBorrowedReviewFlow => _policy.Supported;
    public bool   BorrowedReviewRequiresIndependentSnapshot =>
        _policy.Containment == AcpBorrowedReviewContainment.IndependentSnapshot;
    public string? BorrowedReviewContainment => _policy.Containment switch {
        AcpBorrowedReviewContainment.NativeToolClamp => "native-tool-clamp",
        AcpBorrowedReviewContainment.IndependentSnapshot => CursorBorrowedReviewValidation.Containment,
        _ => null
    };

    /// <summary>ACP / multi-provider vendors have no authoritative reviewer-model resolver yet, so this
    /// stays <see langword="null"/> — the vendor advertises no <c>SupportsReviewerModelResolution</c>
    /// and the server refuses a v3 model override for it, while its existing vendor-only unattended
    /// support is untouched.</summary>
    public IReviewerModelResolver? ReviewerModelResolver => null;

    /// <summary>Delegated to the descriptor's selector, which is the single source of truth for model
    /// selection. Deliberately NOT a type test for <c>NoOpModelSelector</c>: a vendor's own selector
    /// implementation, or a test double, is equally valid and would defeat one.</summary>
    public bool SupportsModelSelection => descriptor.ModelSelector.CanSelectModel;

    public bool IsAvailable() => CliResolver.Exists(descriptor.ResolveBinaryPath(config));

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        LogLaunching(ctx.AgentId, Vendor, ctx.Worktree.Path);
        AcpMetrics.Launches.Add(1);

        ValidateBorrowedArtifact(ctx, _policy);

        // The launch's generated names, created ONCE here and threaded through ctx so the session/new MCP
        // list and the argv's allowlist are built from the SAME instance. Two derivations would produce a
        // launch whose allowlist does not admit its own result channel, and that failure is silent.
        //
        // Deliberately overwrites: LaunchIdentity is not a caller input. Honouring one supplied on the way
        // in would let a requester choose the names whose unguessability is the entire MCP containment.
        ctx = ctx with { LaunchIdentity = LaunchIdentity.ForLaunch(AliasesResultChannel(descriptor)) };

        // The operator capability gate, BEFORE _connectionSource runs.
        //
        // Review caught this: the gate lived only in BuildProcessStartInfo, which only the DEFAULT connection
        // source calls. A supplied source — a test seam today, but the seam is the invariant's boundary either
        // way — was invoked for a disabled daemon or an uncertified vendor version and could spawn directly.
        // "Unbypassable" has to mean before any source, not before the default one. The builder keeps its own
        // check as defence in depth, since a direct builder call is its own path.
        RequireGeminiReviewerCapability(descriptor, config, ctx.IsReviewFlow, _resolveVendorVersion);

        // Fail closed BEFORE _connectionSource spawns a child (a later gate would leak one). Null for
        // a non-review launch; the built MCP list for a valid review flow.
        var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, _policy);

        // A BORROWED-SNAPSHOT launch always takes the Fail policy, whatever the vendor declares.
        //
        // This is the other half of the readable-allowlist change, and without it that change is a
        // read-containment hole rather than a fix. Widening --available-tools to the read tools also
        // widens what a path-taking read tool can be pointed at: Copilot answers an absolute path
        // outside the snapshot with a session/request_permission ("Access paths outside trusted
        // directories"), and AutoApprove grants exactly that shape without inspecting the tool. Probed
        // live: with the read allowlist and an auto-approving bridge, a reviewer read a file outside
        // the snapshot and echoed its contents back through the still-enabled result channel. The
        // snapshot then bounds writes but not reads, which is not what independent-snapshot promises.
        //
        // Fail rather than deny-and-continue, and structural rather than matched on the frame's title
        // (which is vendor prose and can change): under an exclusive read-only allowlist a correct
        // launch raises ZERO interaction frames — verified live across every in-snapshot read — so one
        // arriving means the reviewer is reaching past its boundary, and a reviewer doing that should
        // not go on to produce a review. Same contract Cursor already runs under, so this is a no-op
        // for Cursor and vendor-neutral for anything borrowed-capable later.
        var unattendedInteractionPolicy = ResolveUnattendedInteractionPolicy(ctx, descriptor);

        var runtimeLogger = loggerFactory.CreateLogger<AcpHostedAgentRuntime>();
        var connLogger    = loggerFactory.CreateLogger<AcpConnection>();

        var (input, output, acpProcess) = _connectionSource(ctx);
        var acpConnection = new AcpConnection(input, output, connLogger, config.DebugFrames);

        // Spec-review Finding 4: real production wiring — every launch now gets the
        // permission/elicitation bridge, not the default MethodNotFound/decline.
        var runtime = new AcpHostedAgentRuntime(
            acpConnection,
            acpProcess,
            runtimeLogger,
            agentId: ctx.AgentId,
            requestInteraction: connection.RequestAcpInteractionAsync,
            debugFrames: config.DebugFrames,
            vendor: descriptor.Vendor,
            modelSelector: descriptor.ModelSelector,
            unattendedInteractionPolicy: unattendedInteractionPolicy
        );

        // Review flow: the injected result channel + allowlist. Otherwise unchanged (null today).
        var mcpServers = ctx.IsReviewFlow
            ? descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.SessionNew ? reviewMcp : null
            : descriptor.SupportsMcpServers ? ctx.McpServers : null;

        // An unattended reviewer's MCP surface is a security boundary: the flow-result channel must be
        // present and every flow-STARTING server absent, or a reviewer could start a nested flow. That
        // is enforced in code and pinned byte-exactly by test, but it was not observable at runtime —
        // the resolved list was never logged, so once the reviewer process exited there was no way to
        // answer "what tools did this reviewer actually have?" from the record. Settling that during
        // this session required catching a live process with `ps` and reading its argv, which is not a
        // diagnostic path anyone should need.
        //
        // Names only, deliberately: a server spec carries command paths and an env block, and the
        // result channel's env includes the server URL and the flow agent id. The transport is logged
        // alongside because it decides HOW the surface reaches the vendor — session/new for most, a
        // process argument for Copilot — so the list alone would be ambiguous about what was sent.
        // The emit site is keyed on session establishment, not on StartAsync returning; see
        // LogSurfaceOnceIfEstablished below for why those are not the same moment.
        var surfaceLogged = false;

        // Emitted exactly when the surface has provably crossed the wire, and never before.
        //
        // The predicate is `runtime.SessionId is not null`, which the runtime assigns immediately
        // after a successful `session/new` — the request that carries the MCP list. That makes the
        // audit line true by construction in both directions:
        //
        //   - It cannot fire early. A rejected, malformed or cancelled `initialize` leaves SessionId
        //     null, so no line claims this reviewer held [kcap-flow-result, …] when no reviewer
        //     session ever existed. An audit log that can disagree with what was sent is worse than
        //     no audit log.
        //   - It cannot be skipped late. StartAsync does more work after session/new — notably
        //     awaiting IAcpModelSelector.TrySelectAsync, which propagates OperationCanceledException
        //     while `session/set_config_option` is in flight. Keying the emit on StartAsync's normal
        //     return would drop the record for a session that WAS established and WAS handed the
        //     surface, purely because the daemon token was cancelled a moment later. So the failure
        //     path logs too, before disposal.
        //
        // Copilot's process-argument transport is applied earlier still, at spawn, so a non-null
        // SessionId implies its surface was applied as well.
        void LogSurfaceOnceIfEstablished() {
            if (surfaceLogged || !ctx.IsReviewFlow || runtime.SessionId is null)
                return;

            surfaceLogged = true;

            LogReviewerMcpSurface(
                ctx.AgentId,
                descriptor.Vendor,
                descriptor.ReviewFlowMcpTransport.ToString(),
                string.Join(",", (reviewMcp ?? []).Select(spec => spec.Name)));
        }

        try {
            await runtime.StartAsync(
                ctx.Worktree.Path,
                ctx.Prompt,
                ct,
                ResolveRequestedModel(descriptor, config, ctx),
                mcpServers
            ).ConfigureAwait(false);
        } catch {
            // An established session is a fact the record must keep even though the launch failed.
            LogSurfaceOnceIfEstablished();

            // The runtime owns both the connection and the process; dispose on a failed handshake
            // so a half-started child process is never leaked.
            await runtime.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        LogSurfaceOnceIfEstablished();

        // The runtime IS the transcript source (it implements
        // IAcpTranscriptSource directly) — hand it back on HostedRuntimeStart so the orchestrator can
        // bind + forward without downcasting Runtime.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>
    /// Fail-closed validation + build of the review-flow MCP list, run as the FIRST thing in
    /// <see cref="StartAsync"/> — before <c>_connectionSource</c> can spawn a child. Returns
    internal static AcpUnattendedInteractionPolicy ResolveUnattendedInteractionPolicy(
            RuntimeStartContext ctx, AcpVendorDescriptor descriptor) =>
        !ctx.IsReviewFlow                 ? AcpUnattendedInteractionPolicy.Disabled
        : ctx.IsBorrowedSnapshot          ? AcpUnattendedInteractionPolicy.Fail
        : descriptor.UnattendedInteractionPolicy;

    /// <see langword="null"/> for a non-review launch; for a review flow it throws unless the launch
    /// is safe to run unattended AND has a deliverable result channel AND every allowlist entry is an
    /// auto-approvable read-only server, then returns the built list. Work-location safety is
    /// descriptor-gated: most vendors require a daemon-owned worktree, while a borrowed-review
    /// vendor must provide its own capability clamp. Neither location is itself a filesystem sandbox.
    /// </summary>
    static IReadOnlyList<AcpMcpServerSpec>? ValidateAndBuildReviewFlowMcp(
            RuntimeStartContext ctx, AcpVendorDescriptor descriptor, ResolvedBorrowedReviewPolicy policy) {
        if (!ctx.IsReviewFlow) return null;

        if (!descriptor.SupportsUnattended)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' cannot host an unattended (review-flow) agent.");

        if (ctx.Work != WorkLocation.OwnedWorktree && !policy.Supported)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires an owned worktree, not a borrowed cwd.");
        if (ctx.Work != WorkLocation.OwnedWorktree &&
            policy.Containment == AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires daemon snapshot materialization before spawn.");

        if (descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.Unsupported)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' cannot host a review-flow reviewer: no supported MCP transport for the kcap-flow-result channel.");

        // A blank agent id would still yield a non-empty server list and slip past a count-only guard,
        // so all three result-channel inputs are checked (a dead channel wedges the round).
        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath) || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "Review-flow launch cannot inject the kcap-flow-result channel (missing server url / kcap path / agent id).");

        // The injected MCP set is the reviewer's integration boundary: resolve the allowlist through
        // the SAME authoritative read-only reviewer policy the
        // orchestrator applies to Codex (TryResolveReviewFlowAllowlist — reserved result channel is a
        // no-op, unknown/flow-starting/non-auto-approvable write servers fail the launch fast).
        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var allowlistServerIds, out var rejected))
            throw new InvalidOperationException(
                $"Review-flow reviewer MCP allowlist contains a server that is not auto-approvable: '{rejected}'.");

        return AcpReviewFlowMcp.Build(ctx, allowlistServerIds);
    }

    /// <summary>
    /// Replaces every <see cref="AcpVendorDescriptors.UnmatchableMcpNamePlaceholder"/> with a fresh,
    /// unguessable name — once per launch, so two concurrent agents do not even share one.
    ///
    /// <para><b>Why random rather than a constant.</b> A vendor whose only MCP clamp is an allowlist can be
    /// made to deny everything by allowing a name nothing has. A CONSTANT deny-all name is not that: the
    /// repository being reviewed controls its own MCP config and can name a server exactly that constant,
    /// which was measured to execute. So the deny-all value has to be outside repository control, which
    /// means unpredictable.</para>
    ///
    /// <para>Kept generic rather than Gemini-specific: any vendor whose containment is "allow a name that
    /// cannot exist" needs the same treatment, and a per-vendor copy of this reasoning is how one of them
    /// ends up with a guessable literal again.</para>
    /// </summary>
    static List<string> SubstituteUnmatchableNames(List<string> argv, LaunchIdentity identity) {
        for (var i = 0; i < argv.Count; i++)
            if (argv[i] == AcpVendorDescriptors.UnmatchableMcpNamePlaceholder)
                argv[i] = identity.UnmatchableMcpName;

        return argv;
    }

    /// <summary>
    /// Whether this vendor's result channel must be injected under a per-launch unguessable name.
    ///
    /// <para>True only for Gemini: its MCP gate is an exact-name allowlist that has to admit our own
    /// channel, so the channel's name is itself allowlisted — and a fixed one is matchable by the repository
    /// under review. Every other vendor keeps the canonical id on the wire, so their behaviour is
    /// byte-identical to before the alias existed.</para>
    /// </summary>
    static bool AliasesResultChannel(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor;

    /// <summary>
    /// Merges the per-launch model override with the daemon-wide default —
    /// <paramref name="ctx"/>'s own <c>Model</c> takes precedence when the launch specifies one,
    /// else falls back to <paramref name="descriptor"/>'s <c>ResolveDefaultModel</c>. Mirrors the
    /// existing <c>"default"</c>-sentinel convention <c>CodexLauncher.AddModelArg</c> already uses
    /// for "no override requested" (the UI dispatches the literal string <c>"default"</c>, not an
    /// empty string, when the user hasn't picked a model). The merged value is still a bare family
    /// prefix or an exact <c>modelId</c> — final resolution against the session's
    /// <c>availableModels</c> happens in <see cref="AcpHostedAgentRuntime"/> via
    /// <see cref="Capacitor.Cli.Core.Acp.AcpModelResolver"/>.
    /// </summary>
    static string? ResolveRequestedModel(AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : descriptor.ResolveDefaultModel(config);

    /// <summary>
    /// PURE builder for a real launch's spawn shape — no process side effects. StartRealProcess is
    /// the only production caller; AcpHostedAgentRuntimeFactoryTests calls this directly (Test plan
    /// items 1, 5) to assert on binary path, argv, cwd, and env without a connectionSource
    /// override, which bypasses process-spawning entirely and so could never prove this method's
    /// own correctness.
    /// </summary>
    /// <param name="policy">Test seam ONLY. Production always passes null, which resolves this
    /// machine's platform entry via <see cref="PolicyFor"/> — the same value the advertised capability
    /// is computed from, so argv and advertisement cannot disagree. Tests pass an explicit entry so
    /// the borrowed-snapshot argv is assertable on a platform whose own entry is unsupported.</param>
    /// <param name="readEnvironmentVariable">Test seam ONLY, for the brokered credential. Production
    /// passes null, which reads the daemon's real environment. Tests supply a fake so the borrowed argv
    /// is assertable on a host with no token configured — and, more importantly, so the fail-closed
    /// branch is assertable at all without mutating the test process's own environment.</param>
    internal static ProcessStartInfo BuildProcessStartInfo(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx,
            ResolvedBorrowedReviewPolicy? policy = null,
            Func<string, string?>? readEnvironmentVariable = null,
            Func<string, string?>? resolveGeminiVersion = null) {
        var resolved = policy ?? PolicyFor(descriptor);
        // Defense-in-depth: the orchestrator's UnattendedLaunchPolicy is expected to reject a
        // review-flow launch for a vendor that doesn't support it before this factory ever runs,
        // but the factory doesn't rely on that alone — it refuses to build review-flow argv for an
        // unsupported vendor rather than trusting an external caller always applied the gate.
        if (ctx.IsReviewFlow && !descriptor.SupportsUnattended)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' does not support unattended (review-flow) launches.");

        // Same defense-in-depth, for the containment invariant. StartAsync validates this too, but
        // this is the seam that decides whether the READABLE argv is emitted, so an entry that does
        // not promise independent-snapshot containment must not reach it — the pre-spawn check being
        // one layer up is what would let a direct builder call (a test, a future caller, a refactor
        // that inlines the spawn) produce a readable borrowed argv on an unverified platform.
        ValidateBorrowedArtifact(ctx, resolved);

        // Defense-in-depth for the trust-at-spawn argv appended just below: a borrowed-cwd reviewer
        // would run in the requester's live checkout, so this refuses it here too. StartAsync's
        // pre-spawn validation is the primary gate; this backstops the default spawn path (a
        // non-default connectionSource never reaches this builder).
        if (ctx.IsReviewFlow && ctx.Work != WorkLocation.OwnedWorktree && !resolved.Supported)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires an owned worktree, not a borrowed cwd.");
        if (ctx.IsReviewFlow && ctx.Work != WorkLocation.OwnedWorktree &&
            resolved.Containment == AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires daemon snapshot materialization before spawn.");

        // The launch's generated names. StartAsync threads them in; a direct builder call (a test, a future
        // caller, a refactor that inlines the spawn) gets a fresh set rather than a null deref — but the
        // production path must supply one, or the argv and the session/new MCP list would be built from two
        // different identities and the allowlist would not admit its own result channel.
        var identity = ctx.LaunchIdentity ?? LaunchIdentity.ForLaunch(AliasesResultChannel(descriptor));

        // The fallback identity must also be what every ctx-reading consumer below sees —
        // AcpReviewFlowMcp.Build derives server wire names from ctx.LaunchIdentity, and with ctx still
        // carrying null it falls back to canonical, repository-matchable ids while the argv substitution
        // uses the fresh identity (review finding). The checked value must BE the used value.
        ctx = ctx with { LaunchIdentity = identity };

        // Defence in depth: StartAsync gates before any connection source runs, but a direct builder call
        // (a test, a future caller, a refactor that inlines the spawn) is its own path to an argv.
        RequireGeminiReviewerCapability(descriptor, config, ctx.IsReviewFlow, resolveGeminiVersion);

        var argv = SubstituteUnmatchableNames([.. descriptor.Argv], identity);

        // The comma-joined allowlist value a review launch opens its MCP gate to — null on every other
        // launch. Held here so the whole-vector assertion below asserts the same value the argv got.
        string? reviewGate = null;

        if (ctx.IsReviewFlow) {
            argv.AddRange(descriptor.UnattendedTrustArgv);

            // A review launch REPLACES the deny-all allowlist value with the names of exactly the servers
            // this launch injects — the result channel plus any resolved allowlist servers — as ONE
            // comma-joined value. Replace, never append: the option is array-typed and comma-coerced by
            // the vendor, so a second option occurrence would widen the gate rather than move it. Deriving
            // the value from the BUILT list (not re-deriving from ids) is what keeps the gate and the
            // session/new payload the same set by construction: Build is deterministic given ctx (every
            // name comes from the identity or the registry), so StartAsync's own call for session/new
            // yields the same names — the same-instance identity threading is what guarantees it.
            // Measured on gemini 0.53.0: both admitted servers spawn and reach tools/call, a third
            // injected name outside the gate never spawns. Deny-all is what a launch gets by default and
            // only this arm opens it, the fail-closed direction. Built inside the arm (like Copilot's
            // below) because only these two argv consumers need the list here — for every other vendor
            // session/new is the sole consumer and StartAsync builds it, so validating in the builder too
            // would change direct-builder behavior for vendors whose argv never carries MCP names.
            if (AliasesResultChannel(descriptor)) {
                var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!;
                reviewGate = string.Join(",", reviewMcp.Select(s => s.Name));
                for (var i = 0; i < argv.Count; i++)
                    if (argv[i] == identity.UnmatchableMcpName)
                        argv[i] = reviewGate;
            }

            if (descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.CopilotAdditionalConfig) {
                var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!;
                argv.Add("--additional-mcp-config");
                argv.Add(BuildCopilotAdditionalMcpConfig(reviewMcp));

                // The allowlist stays EXCLUSIVE. A borrowed-snapshot launch widens it with the
                // policy's verified read tools so the reviewer can actually read the snapshot; every
                // other launch (owned worktree, context-only) keeps the flow-result-only clamp, which
                // is what makes the server's read-blind rejection correct rather than paranoid.
                var extraToolIds = ctx.IsBorrowedSnapshot ? resolved.ExtraBorrowedToolIds : [];

                foreach (var toolId in CopilotAvailableToolIds(reviewMcp).Concat(extraToolIds))
                    argv.Add($"--available-tools={toolId}");
            }
        }

        // Every vendor — borrowed-snapshot review included — spawns the ordinary configured binary.
        // Resolving a borrowed reviewer through an exact-build record instead made a vendor
        // auto-update hard-fail the launch. See
        // docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
        var binaryPath = descriptor.ResolveBinaryPath(config);

        // The read boundary. Only a borrowed snapshot is wrapped: every other launch either has no
        // borrowed content to protect or is already confined by the owned worktree it runs in.
        string? stateRoot = null;
        string? brokeredToken = null;

        if (ctx.IsBorrowedSnapshot && resolved.RequiresProcessSandbox) {
            // SnapshotRoot, not Path. When the borrowed cwd is below the repository root the
            // snapshot's Path is the cwd-relative SUBDIRECTORY inside it, so granting Path would
            // leave the reviewer unable to read the snapshot's parent files or its root .git — the
            // original blind-review defect, reappearing for exactly the nested-cwd shape a real
            // launch from `repo/src` produces. Path stays the working directory; the boundary is
            // drawn at the root the daemon materialized.
            var snapshotRoot = ctx.Worktree.SnapshotRoot ?? ctx.Worktree.Path;

            // Defense in depth. The policy already refuses to advertise borrowed review without a
            // brokerable credential, so reaching here without one means the daemon's environment
            // changed under a resolved policy or a caller supplied an entry the host cannot honour.
            // Either way the alternative to failing is a reviewer that falls back to the keychain the
            // profile no longer grants — i.e. a launch that cannot authenticate — so fail here, before
            // a child exists, with a reason an operator can act on.
            // Resolved FRESH per launch, not reused from the startup probe: a token command exists so
            // the operator can supply a rotating credential, and caching the first value would hand a
            // reviewer an expired one for as long as the daemon happened to be up.
            brokeredToken = BorrowedReviewAuthBroker.TryResolve(
                    readEnvironmentVariable ?? Environment.GetEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    "borrowed_review_auth_unavailable: a contained borrowed reviewer authenticates from a "
                  + "brokered token because the sandbox does not grant the keychain. Set one of "
                  + $"{string.Join(", ", BorrowedReviewAuthBroker.SourceVariables)} in the daemon's "
                  + $"environment, or {BorrowedReviewAuthBroker.CommandVariable} to a command that "
                  + "prints one (the supported route for a supervised daemon, whose unit file must not "
                  + "hold a credential).");

            stateRoot = WorktreeManager.VendorStateRootFor(snapshotRoot);

            // Resolve through PATH before deriving grants, and spawn THAT path. The configured value
            // defaults to a bare command name ("copilot"), which is not a path at all: deriving grants
            // from it would resolve against the daemon's current directory and grant that directory
            // recursively, while sandbox-exec separately executed the real binary from PATH. Resolving
            // once and using the result for both the profile and the argv is what keeps "what is
            // granted" and "what runs" the same program.
            var vendorBinary = CliResolver.ResolveExecutable(binaryPath)
                ?? throw new InvalidOperationException(
                    $"borrowed_review_vendor_binary_unresolved: cannot resolve '{binaryPath}' to an "
                  + "executable, so the sandbox cannot be drawn around it.");

            var profile = BorrowedReviewSandbox.BuildProfile(
                snapshotRoot, stateRoot, BorrowedReviewRuntimeRoots.Resolve(vendorBinary));

            argv       = [.. BorrowedReviewSandbox.WrapArgv(profile, vendorBinary, argv)];
            binaryPath = BorrowedReviewSandbox.SandboxExecPath;
        }

        var psi = new ProcessStartInfo(binaryPath, argv) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        if (!string.IsNullOrEmpty(ctx.ServerUrl))
            psi.Environment["KCAP_URL"] = ctx.ServerUrl;

        if (stateRoot is not null) {
            // HOME and TMPDIR both move into the per-launch root, which is what keeps the reviewer
            // away from the user's vendor profile, command history and caches — and what removes the
            // previous profile's blanket /private/var/folders and /dev write grants.
            psi.Environment["HOME"]    = BorrowedReviewSandbox.HomeDirectoryIn(stateRoot);
            psi.Environment["TMPDIR"]  = BorrowedReviewSandbox.TempDirectoryIn(stateRoot);
            psi.Environment[BorrowedReviewAuthBroker.TargetVariable] = brokeredToken;
        }

        // LAST, after every contributor and every substitution, and on psi.ArgumentList rather than the
        // local list — this is the vector the process receives, and nothing between here and Process.Start
        // touches it. Asserting the local list instead would certify something the OS never sees, which is
        // worse than no assertion because it looks like coverage.
        if (AliasesResultChannel(descriptor) && psi.FileName != BorrowedReviewSandbox.SandboxExecPath)
            AssertGeminiArgvIsCanonical(psi.ArgumentList, ctx.IsReviewFlow, identity, reviewGate);

        return psi;
    }

    /// <summary>Pre-spawn check that the descriptor's DECLARED containment agrees with the launch
    /// context it was handed — a code-level invariant (a snapshot-materialized launch reaching a
    /// vendor that does not declare independent-snapshot containment is a wiring bug). It is
    /// deliberately NOT a build-identity check: capability is advertised for whatever build is
    /// installed, so nothing here may consult a version record. See
    /// docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.</summary>
    internal static void ValidateBorrowedArtifact(RuntimeStartContext ctx, ResolvedBorrowedReviewPolicy policy) {
        if (!ctx.IsReviewFlow || !ctx.IsBorrowedSnapshot) return;
        if (policy.Containment != AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException("borrowed_snapshot_containment_mismatch");
    }

    /// <summary>Builds Copilot CLI's process-level stdio MCP config. Copilot silently ignores stdio
    /// servers passed in <c>session/new.mcpServers</c> (measured at call level against CLI 1.0.78 —
    /// see the <see cref="AcpVendorDescriptors.Copilot"/> descriptor comment), but accepts them in
    /// this alternate config shape before the ACP session starts.</summary>
    static string BuildCopilotAdditionalMcpConfig(IReadOnlyList<AcpMcpServerSpec> servers) {
        var mcpServers = new JsonObject();

        foreach (var server in servers) {
            var args = new JsonArray();
            foreach (var arg in server.Args) args.Add(AotSafeJsonString(arg));

            var env = new JsonObject();
            foreach (var item in server.Env) env[item.Name] = AotSafeJsonString(item.Value);

            mcpServers[server.Name] = new JsonObject {
                ["type"]    = AotSafeJsonString("stdio"),
                ["command"] = AotSafeJsonString(server.Command),
                ["args"]    = args,
                ["env"]     = env
            };
        }

        return new JsonObject { ["mcpServers"] = mcpServers }.ToJsonString();
    }

    /// <summary>NativeAOT has no reflection metadata for JsonValue.Create&lt;string&gt;, which is
    /// reached by JsonObject/JsonArray string assignment even though that code works under JIT.
    /// Parse a correctly escaped JSON string fragment so the published daemon stays on the
    /// reflection-free JsonNode path.</summary>
    static JsonNode AotSafeJsonString(string value) =>
        JsonNode.Parse($"\"{JsonEncodedText.Encode(value)}\"")!;

    /// <summary>Copilot's availability filter uses flattened runtime ids (<c>server-tool</c>),
    /// not its permission-pattern syntax (<c>server(tool)</c>). Keep both flow-channel tools plus only the
    /// reviewed-safe tools belonging to the already-validated server list.</summary>
    static IEnumerable<string> CopilotAvailableToolIds(IReadOnlyList<AcpMcpServerSpec> servers) {
        foreach (var server in servers) {
            if (string.Equals(server.Name, KcapMcpRegistry.ReservedResultChannelId, StringComparison.Ordinal)) {
                // Derived from the ordered catalog (the single source of truth), filtered to the
                // unattended-safe tools — this launch is unattended, so a future non-safe catalog
                // entry must not be advertised here. Catalog order is stable and byte-exact-tested.
                foreach (var tool in KcapMcpRegistry.ReservedResultChannelTools) {
                    if (tool.UnattendedSafe) yield return $"{server.Name}-{tool.Name}";
                }
                continue;
            }

            if (string.Equals(server.Name, "kcap-review-context", StringComparison.Ordinal)) {
                yield return "kcap-review-context-get_branch_authored_mcp_configs";
                continue;
            }

            if (!KcapMcpRegistry.ReviewFlowUnattendedSafeTools.TryGetValue(server.Name, out var tools))
                continue;

            foreach (var tool in tools.Order(StringComparer.Ordinal))
                yield return $"{server.Name}-{tool}";
        }
    }

    /// <summary>
    /// The daemon OPERATOR's consent for an unattended Gemini reviewer, keyed on the RESOLVED descriptor's
    /// vendor rather than any requester-supplied text so an alias or case variant cannot slip past.
    ///
    /// <para>Called from BOTH <see cref="StartAsync"/> (before any connection source runs — that is the
    /// boundary) and <see cref="BuildProcessStartInfo"/> (defence in depth for a direct builder call).
    /// No-op for every other vendor and for every interactive launch.</para>
    /// </summary>
    static void RequireGeminiReviewerCapability(
            AcpVendorDescriptor descriptor, DaemonConfig config, bool isReviewFlow,
            Func<string, string?>? resolveVersion) {
        if (!isReviewFlow || !AliasesResultChannel(descriptor)) return;

        // Operator consent is checked FIRST so a disabled daemon never interrogates the vendor binary at all.
        // Review's point: probing an installed-but-wedged vendor while the feature is switched off is a way
        // to hang on a code path the operator opted out of.
        if (!config.GeminiUnattendedReviewerEnabled)
            throw new InvalidOperationException(GeminiReviewerCapability.DenialReason(false, null));

        var version = (resolveVersion ?? ResolveGeminiVersion)(descriptor.ResolveBinaryPath(config));

        if (!GeminiReviewerCapability.IsEnabled(true, version))
            throw new InvalidOperationException(GeminiReviewerCapability.DenialReason(true, version));
    }

    /// <summary>
    /// Gemini's certified-version input: the installed binary's own reported version, or null when it
    /// cannot be determined (which the capability treats as unknown, and therefore denies).
    /// </summary>
    static string? ResolveGeminiVersion(string binaryPath) {
        try {
            var resolved = CliResolver.ResolveExecutable(binaryPath);
            if (resolved is null) return null;

            using var proc = Process.Start(new ProcessStartInfo(resolved, ["--version"]) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return null;

            // Both streams are drained CONCURRENTLY with the wait, and the wait is what bounds this.
            //
            // Review caught a deadlock: the previous shape called ReadToEnd() before WaitForExit(10s), so a
            // vendor that never closed stdout blocked before the timeout could apply — and stderr was
            // redirected but never drained, so filling its buffer wedged the child too. A bounded wait is
            // only bounded if nothing ahead of it can block indefinitely.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(TimeSpan.FromSeconds(10))) {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

                return null;   // a timeout is an UNKNOWN version, which the capability denies
            }

            // The child has exited, so both reads are complete or completing; bounded again so a detached
            // grandchild holding a pipe cannot keep us here.
            if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5))) return null;

            // Extract a version TOKEN from either stream rather than requiring the whole trimmed output to be
            // one. Measured: gemini 0.53.0 prints the version to stdout AND stderr — but requiring exact
            // equality is brittle either way, since the vendor already emits banner lines (skill-conflict
            // warnings) on other paths, and a build that added an "update available" notice would make the
            // gate fail closed and silently disable the reviewer. Review's point, and it applies even though
            // today's format happens to work.
            return proc.ExitCode == 0
                ? ExtractVersionToken(stdout.Result) ?? ExtractVersionToken(stderr.Result)
                : null;
        } catch {
            // Any failure to interrogate the binary is "unknown version", which the capability denies. A
            // throw here would surface as a launch error rather than a coded capability refusal.
            return null;
        }
    }

    /// <summary>
    /// The first dotted-numeric token in <paramref name="output"/>, or null. Deliberately narrow: a
    /// certified-version check compares against an exact set, so anything that is not recognisably a version
    /// must read as UNKNOWN (and therefore denied) rather than as some near-miss string.
    /// </summary>
    internal static string? ExtractVersionToken(string? output) {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var tok = raw.Trim().TrimStart('v', 'V');

            if (tok.Length > 0 && tok.All(c => char.IsAsciiDigit(c) || c == '.') && tok.Contains('.'))
                return tok;
        }

        return null;
    }

    /// <summary>
    /// The complete expected argv for a Gemini launch, as a literal structural sequence with only the
    /// launch-identity values substituted.
    ///
    /// <para><b>Deliberately not derived from <c>descriptor.Argv</c>.</b> If it were, a token newly added to
    /// the descriptor would appear on both sides of the comparison and pass while the launch had changed —
    /// an oracle derived from the thing under test. Written out so a fourth contributor to the argv, or an
    /// edited constant, goes red.</para>
    /// </summary>
    internal static string[] ExpectedGeminiArgv(bool isReviewFlow, LaunchIdentity identity, string? reviewGate) =>
        isReviewFlow
            ? ["--experimental-acp", "--skip-trust",
               "--allowed-mcp-server-names",
               reviewGate ?? throw new InvalidOperationException(
                   "gemini_review_gate_missing: a review launch's expected argv needs the comma-joined "
                 + "allowlist value the launch computed; asserting against a re-derived one would let the "
                 + "gate and the assertion drift apart."),
               "--approval-mode", "yolo"]
            : ["--experimental-acp", "--skip-trust",
               "--allowed-mcp-server-names", identity.UnmatchableMcpName];

    /// <summary>
    /// Asserts the WHOLE emitted argv against <see cref="ExpectedGeminiArgv"/>.
    ///
    /// <para><b>This is not an input filter.</b> Nothing untrusted reaches this argv: the launch context
    /// carries no arguments field, no configuration key supplies extra arguments, and the only contributors
    /// are two <c>ImmutableArray</c> descriptor constants plus a Copilot-only branch. It asserts an invariant
    /// about CODE — that adding a contributor cannot silently produce a Gemini launch which prompts for
    /// approval or widens the MCP gate. It should be unfalsifiable today; if it ever throws, a contributor
    /// was added without reading this.</para>
    ///
    /// <para>The review gate value is an INPUT, not re-derived here: the template's job is catching a new
    /// argv contributor, while gate↔session/new parity is pinned separately by the launch tests, against
    /// the built server list.</para>
    ///
    /// <para>Whole-vector rather than a scan for dangerous options, because a template fails on any new
    /// token whatever its spelling — so it needs no model of the vendor's option grammar, where camel-case
    /// expansion and boolean negation both make an enumerated key list unprovable.</para>
    /// </summary>
    internal static void AssertGeminiArgvIsCanonical(
            IReadOnlyList<string> argv, bool isReviewFlow, LaunchIdentity identity, string? reviewGate) {
        var expected = ExpectedGeminiArgv(isReviewFlow, identity, reviewGate);

        if (argv.SequenceEqual(expected)) return;

        throw new InvalidOperationException(
            $"gemini_launch_argv_not_canonical: built [{string.Join(" ", argv)}] but this launch shape is "
          + $"[{string.Join(" ", expected)}]. A contributor to the Gemini argv was added or changed; a "
          + "review launch must carry exactly one --approval-mode yolo and exactly one allowlist entry "
          + "naming exactly the servers it injects, and an interactive launch must carry the deny-all name "
          + "and no approval mode (the Gemini reviewer design spec §3.3a).");
    }

    /// <summary>
    /// The REAL, production process-spawning path — unchanged in behavior from this factory's
    /// prior Cursor-only shape, just descriptor-parameterized and delegating its argv/ProcessStartInfo
    /// construction to the pure <see cref="BuildProcessStartInfo"/> (spec-review Finding 4). Spawns
    /// the descriptor's binary + argv and returns its stdio streams plus an
    /// <see cref="AcpChildProcess"/> lifecycle wrapper.
    /// </summary>
    static (Stream Input, Stream Output, IAcpProcess Process) StartRealProcess(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx, ILoggerFactory loggerFactory) {
        var psi = BuildProcessStartInfo(descriptor, config, ctx);

        // Materialize what the pure builder declared. Only a sandboxed borrowed launch carries these,
        // and the vendor cannot create them itself: HOME must exist before the runtime starts, and the
        // profile grants the state root rather than its parent.
        if (psi.FileName == BorrowedReviewSandbox.SandboxExecPath &&
            psi.Environment.TryGetValue("HOME", out var sandboxHome) && sandboxHome is { Length: > 0 })
            BorrowedReviewSandbox.CreateStateDirectories(
                Path.GetDirectoryName(sandboxHome)
                ?? throw new InvalidOperationException("borrowed_review_state_root_unresolved"));

        var process = Process.Start(psi)
         ?? throw new InvalidOperationException($"Failed to start '{psi.FileName} {string.Join(' ', psi.ArgumentList)}' (Process.Start returned null).");

        var processLogger = loggerFactory.CreateLogger<AcpChildProcess>();
        var acpProcess    = new AcpChildProcess(process, processLogger, config.DebugFrames, descriptor.Vendor);

        return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, acpProcess);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP hosted agent launch: agentId={AgentId} vendor={Vendor} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string vendor, string cwd);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP reviewer MCP surface: agentId={AgentId} vendor={Vendor} transport={Transport} servers=[{ServerNames}]")]
    partial void LogReviewerMcpSurface(string agentId, string vendor, string transport, string serverNames);
}
