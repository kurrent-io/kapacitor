using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Cursor;
using Capacitor.Cli.Daemon.Harness.Gemini;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Capacitor.Cli.Daemon.Harness.OpenCode;
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
/// <c>AcpHostedAgentRuntimeFactoryTests</c> can construct THIS class for real and drive its
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
        Func<string, string?>? resolveVendorVersion = null,
        // Test seam ONLY for the per-stage launch-handshake cap. Production passes null → the
        // TimeProvider.System every other daemon-local timing decision uses.
        TimeProvider? timeProvider = null
    ) : IHostedAgentRuntimeFactory {
    readonly Func<string, string?>? _resolveVendorVersion = resolveVendorVersion;
    readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
    /// Whether this daemon advertises the vendor as unattended-capable. For a gated reviewer (Gemini,
    /// Kiro) this also requires the operator's capability gate AND an accepted vendor version, so a
    /// daemon that has not opted in is never selected as a reviewer host in the first place.
    ///
    /// <para>Advertisement is an OPTIMISATION, not the boundary — the authoritative check is in
    /// <see cref="BuildProcessStartInfo"/>, immediately before the spawn, because an explicit
    /// <c>vendor: "gemini"</c> request can reach a launch without consulting advertisement.</para>
    /// </summary>
    public bool SupportsUnattended => DescribeUnattendedSupport().Supported;

    /// <summary>
    /// The advertisement decision and its reason from ONE pass over the gate ladder — see
    /// <see cref="IHostedAgentRuntimeFactory.DescribeUnattendedSupport"/>.
    ///
    /// <para>A descriptor that never claimed unattended support reports <c>(false, null)</c> — nothing
    /// withheld, nothing to fix. A refused reviewer reports the same text
    /// <see cref="RequireReviewerCapability"/> throws, because it is the same call.</para>
    /// </summary>
    public UnattendedSupport DescribeUnattendedSupport() {
        if (!descriptor.SupportsUnattended) return new(false, null);

        var withheld = ReviewerRefusal(descriptor, config, _resolveVendorVersion);

        return new(withheld is null, withheld);
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

    public string CliPath => descriptor.ResolveBinaryPath(config);

    public bool IsAvailable() => new CliResolver(config.Binaries).Exists(CliPath);

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
        RequireReviewerCapability(descriptor, config, ctx.IsReviewFlow, _resolveVendorVersion);

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

        // Nullable: a post-spawn/pre-StartAsync construction failure below (acpConnection or the
        // runtime itself) must still be able to tear down what THIS launch already obtained, even
        // though no AcpHostedAgentRuntime exists yet to own that ordered cleanup. Widened region —
        // design spec §3.2's "post-spawn/pre-protected-region construction failure currently leaks
        // the child" fix.
        AcpConnection? acpConnection = null;
        AcpHostedAgentRuntime runtime;

        try {
            acpConnection = new AcpConnection(input, output, connLogger, config.DebugFrames);

            // Reconnect eligibility (reconnect spec §4), decided at construction: probe-verified
            // vendor AND an interactive launch AND the kill switch off. The remaining conjuncts
            // (advertised loadSession, the resume cap) are runtime facts the runtime itself gates on
            // per incident. The spawn closure re-invokes THIS launch's connection source, so a
            // candidate is spawned by the same code path — argv, env, cwd — as the original child, and
            // carries no registration/forwarder/slot side effects (§6.2's pure-spawn contract).
            var reconnect = descriptor.SupportsReconnectResume && !ctx.IsReviewFlow && config.AcpReconnectEnabled
                ? new AcpReconnectSupport { Spawn = () => _connectionSource(ctx) }
                : null;
            // PidCallbacks defaults to AcpPidRecordCallbacks.Unwired — FAIL-CLOSED (code-review r1/r2):
            // a crash landing in the orchestrator's wiring window fails its attempts (§6.2's
            // record-before-any-handshake MUST) rather than proceeding with an unrecorded candidate,
            // and the orchestrator replaces recorder+clearer in ONE atomic reference assignment so no
            // partially-wired state is ever observable.

            // Spec-review Finding 4: real production wiring — every launch now gets the
            // permission/elicitation bridge, not the default MethodNotFound/decline.
            runtime = new AcpHostedAgentRuntime(
                acpConnection,
                acpProcess,
                runtimeLogger,
                agentId: ctx.AgentId,
                requestInteraction: connection.RequestAcpInteractionAsync,
                // Drives the handshake's per-stage caps (RunHandshakeStageAsync), so a test's
                // FakeTimeProvider controls them without a real 90-second wait.
                timeProvider: _timeProvider,
                debugFrames: config.DebugFrames,
                vendor: descriptor.Vendor,
                modelSelector: descriptor.ModelSelector,
                unattendedInteractionPolicy: unattendedInteractionPolicy,
                reconnect: reconnect,
                // Built from the SAME spec list session/new receives, so the expected set is what was
                // actually sent rather than a re-derivation of it.
                mcpSurfaceMonitor: KiroMcpSurfaceMonitor.For(
                    descriptor, ctx.IsReviewFlow, reviewMcp, ctx.LaunchIdentity),
                // NULL for every launch with nothing to clean up, rather than a lambda that no-ops
                // internally: the runtime keys its ordered-teardown path (await the reap, confirm child
                // exit) on this being non-null, so an always-supplied callback made every ACP launch of
                // every vendor pay that wait. The factory can only clean up launches that FAILED, so a
                // successful review's home needs this hook — it holds review context, and would
                // otherwise survive until a later daemon epoch swept it.
                onDisposed: ReviewerLaunchTimeoutSeconds(descriptor, config, ctx) is not null
                    ? (Action)(() => DeleteReviewerIsolatedDirectory(descriptor, config, ctx, _logger))
                    : null,
                // The second half of the launch bound. The deadline below covers spawn through the
                // handshake; StartAsync deliberately does NOT await the first turn, so a peer that
                // completes initialize and then wedges on the credential path would otherwise be
                // unbounded. Time-to-first-OUTPUT, never turn completion — a real review runs long.
                firstOutputDeadline: ReviewerLaunchTimeoutSeconds(descriptor, config, ctx) is { } firstOutputSeconds
                    ? TimeSpan.FromSeconds(firstOutputSeconds)
                    : null,
                // Whether a silent turn has a person watching it. Not derivable from the deadline
                // above: only some vendors carry one, so a reviewer without it would otherwise be
                // told, in a transcript that is consumed as review output, to keep waiting.
                isReviewFlow: ctx.IsReviewFlow,
                // The set AllowlistedAutoApprove admits, built from the SAME injected specs and identity
                // the trust argv is built from. Two derivations would let the reviewer be TRUSTED to call
                // a tool the policy then refuses to approve — a round that dies on its own result call.
                admittedToolIds: descriptor.UnattendedInteractionPolicy
                                     == AcpUnattendedInteractionPolicy.AllowlistedAutoApprove
                              && ctx.IsReviewFlow && reviewMcp is { Count: > 0 } && ctx.LaunchIdentity is { } id
                    ? UnattendedToolAdmission.AdmittedFor(reviewMcp, id)
                    : null,
                // Launch-time permission preset — resolved for NON-review-flow launches only (a review
                // flow runs under its own containment posture, never a preset). A malformed token
                // resolves to null (inert). The audit sink is fire-and-forget; ServerConnection swallows
                // its own send faults so the discarded task never faults.
                acpPermissionPreset: !ctx.IsReviewFlow
                                  && AcpPermissionPresets.TryResolve(ctx.AcpPermissionPreset, out var resolvedPreset)
                    ? resolvedPreset
                    : null,
                notifyAutoApproval: notice => _ = connection.NotifyAcpAutoApprovalAsync(notice),
                // The launch's own policy, evaluated at the permission seam ahead of the preset. Null
                // for an ungoverned launch, which leaves the bridge's arms exactly as they were.
                policySnapshot: ctx.PolicySnapshot,
                notifyPolicyDecision: evt => _ = connection.AppendAgentRunEventAsync(ctx.AgentId, evt),
                // The same directory the child is spawned in, so a relative tool-call path resolves to
                // the file the agent will actually touch — an unresolvable one evaluates as Other and
                // slips past every path rule.
                policyCwd: ctx.Worktree.Path
            );

            // MUST precede StartAsync below: the handshake's SetLaunchStage stamps are no-ops against
            // a null clock, so a later assignment silently defeats every stage stamp for the launch.
            runtime.ActivityClock = ctx.ActivityClock;
        } catch {
            // No AcpHostedAgentRuntime was ever built (a throw here means construction never reached
            // its own assignment) — nothing owns ordered teardown, so tear down directly whatever this
            // launch DID already obtain rather than leaking the spawned child.
            await DisposeUnclaimedSpawnAsync(acpConnection, acpProcess).ConfigureAwait(false);
            throw;
        }

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

        // ONE absolute budget across spawn -> initialize -> session/new -> model selection, not a
        // fresh timeout per stage: re-deriving it per stage lets a slow sequence approach a multiple
        // of it. It does NOT cover the first turn — StartAsync enqueues that without awaiting, by
        // design, so the turn is bounded separately by the runtime's first-OUTPUT watchdog.
        //
        // Why this exists at all. Operator-managed authentication is a launch PRECONDITION, not an
        // invariant: a credential can expire or be revoked between the operator's login and a review
        // three weeks later. Measured, an unauthenticated kiro-cli does not fail — it prints
        // "Opening browser..." and STAYS ALIVE FOREVER. Nothing else here bounds that: the runtime
        // bounds only its settlement wait, and a server-side round timeout would fail the round while
        // leaving this child, and its transcript-bearing home, behind.
        using var launchDeadline = ReviewerLaunchDeadline(descriptor, config, ctx, ct);

        try {
            await runtime.StartAsync(
                ctx.Worktree.Path,
                ctx.Prompt,
                launchDeadline?.Token ?? ct,
                ResolveRequestedModel(descriptor, config, ctx),
                mcpServers,
                ModelOwedAnExplanation(ctx)
            ).ConfigureAwait(false);
        } catch (OperationCanceledException ex) when (launchDeadline is { IsCancellationRequested: true }
                                                && !ct.IsCancellationRequested) {
            LogSurfaceOnceIfEstablished();

            // Terminate EXPLICITLY, and before disposal. Disposal alone is not a reap — it releases
            // our handles, which for a child that is alive and silent (the shape this branch exists
            // for) leaves it running. Caught by the alive-but-silent test, which asserted termination
            // rather than just the coded error.
            try {
                await acpProcess.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception termEx) {
                _logger.LogDebug(termEx,
                    "ACP: failed to reap {Vendor} reviewer after its launch deadline expired.", descriptor.Vendor);
            }

            // Only after the child is gone: deleting under a live child leaves it writing into an
            // unlinked path. The directory holds review state, so this is disposal, not disk hygiene.
            // No explicit delete here: runtime.DisposeAsync runs the ordered cleanup (await the
            // reap, confirm exit, then delete), and deleting again afterwards would bypass exactly
            // the exit-confirmed gate that ordering exists to enforce. DisposeAsync now unconditionally
            // awaits any claimed reap, so a Verdict checked immediately afterward is final. Its
            // teardown fault is contained (finding 3) so it cannot bypass reclassification below.
            await DisposeRuntimeContainedAsync(runtime).ConfigureAwait(false);

            // Design spec §3.2: the deadline catch consults the verdict IDENTICALLY to the general
            // catch below — a reap racing the launch deadline must not bypass reclassification and
            // surface the generic timeout text instead of the coded reason.
            if (ReclassifyIfReaped(runtime, ex) is { } deadlineReclassified) throw deadlineReclassified;

            throw new InvalidOperationException(
                $"{descriptor.Vendor}_reviewer_launch_timeout: the reviewer did not complete its first "
              + $"prompt within {ReviewerLaunchTimeoutSeconds(descriptor, config, ctx)}s. The child was "
              + $"terminated and its isolated directory removed. {ReviewerLaunchTimeoutHint(descriptor)}");
        } catch (Exception ex) {
            // An established session is a fact the record must keep even though the launch failed.
            LogSurfaceOnceIfEstablished();

            // The runtime owns both the connection and the process; dispose on a failed handshake
            // so a half-started child process is never leaked.
            // As above: disposal owns the exit-confirmed deletion, and now unconditionally awaits any
            // claimed reap first, so the Verdict check right after is never racing it. Its teardown
            // fault is contained (finding 3) so it cannot bypass reclassification below.
            await DisposeRuntimeContainedAsync(runtime).ConfigureAwait(false);

            // Design spec §3.2: the single reclassification seam — a published Verdict means this
            // launch was reaped, and its coded reason becomes the exception's headline instead of
            // whatever StartAsync actually threw. No verdict ⇒ ex propagates byte-identically.
            if (ReclassifyIfReaped(runtime, ex) is { } reclassified) throw reclassified;

            throw;
        }

        LogSurfaceOnceIfEstablished();

        // The runtime IS the transcript source (it implements
        // IAcpTranscriptSource directly) — hand it back on HostedRuntimeStart so the orchestrator can
        // bind + forward without downcasting Runtime.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>
    /// A reviewer launch reaped by a containment tripwire during <see cref="StartAsync"/> (design
    /// spec §3.2) — the daemon's coded <see cref="AcpHostedAgentRuntime.TerminationVerdict"/>
    /// reclassified as the launch failure's headline, with the transport-level cause folded in
    /// parenthetically rather than lost. <see cref="Exception.InnerException"/> is whatever
    /// <see cref="AcpHostedAgentRuntime.StartAsync"/> (or the launch deadline) actually threw, so
    /// daemon-side diagnostics keep everything.
    /// </summary>
    internal sealed class AcpReviewerReapedException(string message, Exception inner)
        : InvalidOperationException(message, inner);

    /// <summary>
    /// Disposes the runtime with its teardown fault CONTAINED (design spec §3.2 / finding 3).
    /// <see cref="AcpHostedAgentRuntime.DisposeAsync"/> can propagate a connection/process disposal
    /// fault; letting that escape a launch-failure catch would skip <see cref="ReclassifyIfReaped"/>
    /// and replace the coded verdict with a teardown exception. The published verdict is already final
    /// by the time this runs (disposal awaits the reap first), so a disposal fault is pure teardown
    /// noise — logged at Debug, never rethrown, so reclassification always runs.
    /// </summary>
    async Task DisposeRuntimeContainedAsync(AcpHostedAgentRuntime runtime) {
        try {
            await runtime.DisposeAsync().ConfigureAwait(false);
        } catch (Exception disposeEx) {
            _logger.LogDebug(disposeEx,
                "ACP: runtime disposal faulted during launch-failure teardown; the coded verdict (if any) still governs reclassification.");
        }
    }

    /// <summary>
    /// The single reclassification seam (design spec §3.2): after ordered disposal — which now
    /// unconditionally awaits any claimed reap (<see cref="AcpHostedAgentRuntime.DisposeAsync"/>) — a
    /// published <see cref="AcpHostedAgentRuntime.Verdict"/> means this launch was reaped, and its
    /// coded reason becomes the exception's headline instead of whatever the caller actually caught.
    /// Returns <see langword="null"/> when no verdict was ever published, so the caller's own
    /// exception propagates completely untouched (byte-identical no-verdict path).
    /// </summary>
    static AcpReviewerReapedException? ReclassifyIfReaped(AcpHostedAgentRuntime runtime, Exception ex) =>
        runtime.Verdict is { } verdict
            ? new AcpReviewerReapedException($"{verdict.Reason} (transport: {DescribeTransportCause(ex)})", ex)
            : null;

    /// <summary>
    /// The reclassified message's transport half: the sanitized handshake cause when
    /// <see cref="AcpHostedAgentRuntime.StartAsync"/>'s own wrapper produced one, else the caught
    /// exception's own message run through the SAME sanitizer — covers every stage the wrapper
    /// doesn't reach, namely model selection (never wrapped at all) and a launch deadline firing
    /// mid-RPC (whose <see cref="OperationCanceledException"/> the wrapper deliberately never
    /// touches either). Calls <see cref="AcpHostedAgentRuntime.SanitizeForForward"/> directly rather
    /// than a local copy, so a future fix to that sanitizer (e.g. Unicode-safe truncation) reaches
    /// this call site too instead of silently missing it.
    /// </summary>
    static string DescribeTransportCause(Exception ex) =>
        (ex as AcpHostedAgentRuntime.AcpHandshakeFailedException)?.TransportMessage
            ?? AcpHostedAgentRuntime.SanitizeForForward(ex.Message);

    /// <summary>
    /// Disposes whatever a launch already obtained when no <see cref="AcpHostedAgentRuntime"/> was
    /// ever built to own ordered teardown — the widened pre-StartAsync construction-failure guard's
    /// cleanup unit (design spec §3.2: "a post-spawn/pre-protected-region construction failure
    /// currently leaks the child"). Best-effort throughout: a launch that already failed must not be
    /// masked by a cleanup fault. <paramref name="process"/> is terminated before either is disposed,
    /// mirroring <see cref="AcpHostedAgentRuntime.DisposeAsync"/>'s own ordering (a reap is not a
    /// disposal — releasing handles under a still-alive child just leaves it running).
    /// </summary>
    internal static async Task DisposeUnclaimedSpawnAsync(AcpConnection? connection, IAcpProcess? process) {
        // Each cleanup step is INDEPENDENTLY guarded (finding 4): a throwing connection disposal must
        // neither skip the process disposal below it nor escape to mask the construction failure that
        // brought us here — the "best-effort throughout" contract. The prior shape awaited
        // connection.DisposeAsync with no guard, so a throwing stream disposal did both.
        if (process is not null) {
            try {
                await process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch {
                // Best-effort — the launch's own construction failure is the error that matters.
            }
        }

        if (connection is not null) {
            try {
                await connection.DisposeAsync().ConfigureAwait(false);
            } catch {
                // Best-effort — must not skip the process disposal below or mask the launch failure.
            }
        }

        if (process is not null) {
            try {
                await process.DisposeAsync().ConfigureAwait(false);
            } catch {
                // Best-effort — the same contract: a cleanup fault never replaces the real error.
            }
        }
    }

    /// <summary>
    /// Design spec §3.5's non-empty-reason fallback formula: <c>launch_failed:{exception type name}
    /// — see daemon log</c> for a null/whitespace reason, else the reason verbatim. Task 4's
    /// orchestrator mapping owns APPLYING this to whatever a launch failure ultimately reports, but a
    /// pre-StartAsync factory failure — thrown before any runtime (hence any verdict) exists — is
    /// exactly the case the fallback exists to cover, so the formula lives here rather than being
    /// duplicated at every call site.
    /// </summary>
    internal static string DescribeLaunchFailure(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? FormatFallbackLaunchReason(ex.GetType().Name) : ex.Message;

    /// <summary>
    /// The SINGLE owner of the §3.5 fallback reason format (finding 5). Both the exception-backed
    /// path (<see cref="DescribeLaunchFailure"/>) and the reason-backed path
    /// (<see cref="AgentOrchestrator.MapLaunchFailureReason"/>) route their fallback through here, so
    /// the two can never drift to different <c>launch_failed:…</c> shapes. <paramref name="token"/> is
    /// the identifier for WHAT produced the empty reason — an exception type name, or a caller-supplied
    /// source string.
    /// </summary>
    internal static string FormatFallbackLaunchReason(string token) => $"launch_failed:{token} — see daemon log";

    /// <summary>
    /// The launch budget, in seconds, for a review launch whose vendor gets one — Kiro and OpenCode,
    /// which both own a per-launch isolated directory that a wedged child would strand. Null for every
    /// other launch, which keeps their behaviour byte-identical.
    ///
    /// <para>The ONE place the "does this vendor have a reviewer budget" question is answered, so the
    /// deadline, the <c>firstOutputDeadline</c> and the <c>onDisposed</c> cleanup cannot come to
    /// different conclusions — a budget without the matching cleanup hook strands exactly the directory
    /// the budget fired to reclaim.</para>
    /// </summary>
    internal static int? ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx) {
        if (!ctx.IsReviewFlow) return null;

        if (descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor)
            return config.KiroReviewerLaunchTimeoutSeconds;

        if (descriptor.Vendor == AcpVendorDescriptors.OpenCode.Vendor)
            return config.OpenCodeReviewerLaunchTimeoutSeconds;

        return null;
    }

    /// <summary>The single absolute budget for a gated review launch, or null for every other launch.
    /// Linked to the caller's token so a real shutdown still wins and is not misreported as a
    /// timeout.</summary>
    static CancellationTokenSource? ReviewerLaunchDeadline(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx, CancellationToken ct) {
        if (ReviewerLaunchTimeoutSeconds(descriptor, config, ctx) is not { } seconds) return null;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(seconds));
        return linked;
    }

    /// <summary>The vendor-specific tail of the launch-timeout error: what the operator should check.
    /// Both vendors' CLIs share the failure this bound exists for — an expired credential that waits on
    /// an interactive login instead of failing — so the shape is the same and only the binary
    /// differs.</summary>
    static string ReviewerLaunchTimeoutHint(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.OpenCode.Vendor
            ? "An opencode whose credential has expired can wait on an interactive login rather than "
            + "failing, which is the shape this bound exists for — check that the daemon user's "
            + "opencode is still authenticated (`opencode auth login`)."
            : "A kiro-cli whose credential has expired stays alive on an interactive browser prompt "
            + "rather than failing, which is the shape this bound exists for — check that the daemon "
            + "user's kiro-cli is still authenticated.";

    /// <summary>Best-effort disposal of a failed launch's isolated reviewer directory. Never throws: a
    /// directory we cannot delete must not replace the launch's real error with a cleanup one.
    ///
    /// <para>Each vendor's own delete is used rather than a shared recursive one, because each refuses a
    /// path outside ITS root — a shared helper would have to be told which root to check against, and
    /// that argument is exactly the thing worth not getting wrong.</para></summary>
    static void DeleteReviewerIsolatedDirectory(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx, ILogger log) {
        if (!ctx.IsReviewFlow) return;

        var stateDir = ReviewerStateDir(config);
        var epoch    = config.DaemonEpoch ?? "unpinned";

        if (descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor) {
            KiroReviewerHome.Delete(
                Path.Combine(KiroReviewerHome.RootFor(stateDir),
                             KiroReviewerHome.NameFor(epoch, ctx.AgentId)),
                stateDir, log);
            return;
        }

        if (descriptor.Vendor == AcpVendorDescriptors.OpenCode.Vendor) {
            OpenCodeReviewerConfigDir.Delete(
                Path.Combine(OpenCodeReviewerConfigDir.RootFor(stateDir),
                             OpenCodeReviewerConfigDir.NameFor(epoch, ctx.AgentId)),
                stateDir, log);
        }
    }

    internal static AcpUnattendedInteractionPolicy ResolveUnattendedInteractionPolicy(
            RuntimeStartContext ctx, AcpVendorDescriptor descriptor) =>
        !ctx.IsReviewFlow                 ? AcpUnattendedInteractionPolicy.Disabled
        : ctx.IsBorrowedSnapshot          ? AcpUnattendedInteractionPolicy.Fail
        : descriptor.UnattendedInteractionPolicy;

    /// <summary>
    /// Fail-closed validation + build of the review-flow MCP list, run as the FIRST thing in
    /// <see cref="StartAsync"/> — before <c>_connectionSource</c> can spawn a child. Returns
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
    /// <summary>Vendors whose injected MCP servers carry PER-LAUNCH wire names.
    ///
    /// <para>Two different reasons, deliberately served by one mechanism. Gemini needs unguessable
    /// names because its MCP gate is an exact-name allowlist the reviewed repository could declare a
    /// server under. Kiro needs them because its MCP surface tripwire compares reported server names
    /// against the injected set, and a canonical public id is a string any other source could also
    /// produce — aliasing is what makes that comparison close to an identity check rather than a
    /// string match.</para></summary>
    internal static bool AliasesResultChannel(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor
     || descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor;

    /// <summary>Vendors whose ARGV carries an exact-name MCP allowlist a review launch must widen to
    /// exactly its injected set — Gemini alone.
    ///
    /// <para><b>Split out of <see cref="AliasesResultChannel"/> rather than reusing it.</b> That one
    /// predicate used to gate four separate behaviours; Kiro aliases but has no such flag, so running
    /// the placeholder substitution or the canonical-argv assertion for it would assert against
    /// machinery it does not have, and route it through Gemini's capability gate.</para></summary>
    internal static bool UsesMcpNameAllowlistArgv(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor;

    /// <summary>Whether the model reaching the runtime is the launch's own pick rather than the
    /// daemon-wide default. The UI dispatches the literal string <c>"default"</c>, not an empty one,
    /// when the user picked nothing (the same sentinel convention <c>CodexLauncher.AddModelArg</c>
    /// reads).</summary>
    static bool LaunchPickedTheModel(RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model the user is owed an explanation for if selection does not apply it, or null
    /// when nothing was picked. A default the vendor does not publish is deliberately not disclosed —
    /// nobody asked for it, and a note on every launch trains the user to ignore it. A pick the
    /// orchestrator already cleared is still disclosed: that path drops it precisely because the
    /// vendor cannot apply one, so it is the case most in need of saying so.</summary>
    static string? ModelOwedAnExplanation(RuntimeStartContext ctx) =>
        ctx.DroppedModelPick is { Length: > 0 } dropped ? dropped
            : LaunchPickedTheModel(ctx) ? ctx.Model
            : null;

    /// <summary>The merged value is a bare family prefix or an exact <c>modelId</c>; resolution
    /// against the session's <c>availableModels</c> happens in <see cref="AcpHostedAgentRuntime"/>
    /// via <see cref="Capacitor.Cli.Core.Acp.AcpModelResolver"/>.</summary>
    static string? ResolveRequestedModel(AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx) =>
        LaunchPickedTheModel(ctx) ? ctx.Model : descriptor.ResolveDefaultModel(config);

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
        RequireReviewerCapability(descriptor, config, ctx.IsReviewFlow, resolveGeminiVersion);

        var argv = SubstituteUnmatchableNames([.. descriptor.Argv], identity);

        // The comma-joined allowlist value a review launch opens its MCP gate to — null on every other
        // launch. Held here so the whole-vector assertion below asserts the same value the argv got.
        string? reviewGate = null;

        if (ctx.IsReviewFlow) {
            // A vendor whose trust argv depends on what this launch injects builds it from the SAME
            // spec list session/new gets and the SAME identity. Deriving it from server ids instead
            // would be a second derivation of the same names, and that failure is silent: the
            // reviewer starts normally and can never call its own channel.
            if (descriptor.UnattendedTrustArgvBuilder is { } buildTrustArgv)
                argv.AddRange(buildTrustArgv(
                    ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!, identity));
            else
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
            if (UsesMcpNameAllowlistArgv(descriptor)) {
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
            var vendorBinary = new CliResolver(config.Binaries).ResolveExecutable(binaryPath)
                ?? throw new InvalidOperationException(
                    $"borrowed_review_vendor_binary_unresolved: cannot resolve '{binaryPath}' to an "
                  + "executable, so the sandbox cannot be drawn around it.");

            var profile = BorrowedReviewSandbox.BuildProfile(
                snapshotRoot, stateRoot, BorrowedReviewRuntimeRoots.Resolve(vendorBinary, config.Home));

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

        // The isolated home is what suppresses the operator's GLOBAL MCP servers — the flows server
        // among them, which would let a reviewer start nested review flows. Created here rather than
        // left to the vendor: it must exist, and be owner-only, before the child writes the first
        // transcript line into it. Review launches only; an interactive hosted Kiro must behave as the
        // user's own session does, global servers included.
        if (ctx.IsReviewFlow && descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor)
            psi.Environment["KIRO_HOME"] = KiroReviewerHome.Create(
                ReviewerStateDir(config), config.DaemonEpoch ?? "unpinned", ctx.AgentId);

        // OpenCode's launch controls are env-shaped rather than argv-shaped (`opencode acp` accepts
        // none of the global flags), so its whole posture lives here. Unlike Kiro's branch above this
        // is NOT review-only: the plugin suppression it applies is what keeps an INTERACTIVE hosted
        // session from being captured twice.
        if (descriptor.Vendor == AcpVendorDescriptors.OpenCode.Vendor) {
            OpenCodeLaunchEnvironment.Apply(psi.Environment);

            // A review launch additionally gets the isolated config dir, project-config suppression and
            // the scoped permission table. Built from the SAME injected spec list session/new receives —
            // deriving the permission entries from server ids instead would be a second derivation of
            // the same names, and that failure is silent: the reviewer starts normally and its own
            // result channel is simply absent from its toolset (measured).
            if (ctx.IsReviewFlow)
                OpenCodeLaunchEnvironment.ApplyReviewer(
                    psi.Environment,
                    OpenCodeReviewerConfigDir.Create(
                        ReviewerStateDir(config), config.DaemonEpoch ?? "unpinned", ctx.AgentId),
                    ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!);
        }

        if (stateRoot is not null) {
            // HOME and TMPDIR both move into the per-launch root, which is what keeps the reviewer
            // away from the user's vendor profile, command history and caches — and what removes the
            // previous profile's blanket /private/var/folders and /dev write grants.
            var sandboxHome = BorrowedReviewSandbox.HomeDirectoryIn(stateRoot);

            psi.Environment["HOME"]    = sandboxHome;
            psi.Environment["TMPDIR"]  = BorrowedReviewSandbox.TempDirectoryIn(stateRoot);
            // The home is only where a nested kcap would DERIVE its root, so an operator with
            // KCAP_CONFIG_DIR exported had the reviewer reading the real profile by inheritance.
            psi.Environment[ConfigRoot.ConfigDirEnvVar] = ConfigRoot.UnderHome(sandboxHome).Directory;
            psi.Environment[BorrowedReviewAuthBroker.TargetVariable] = brokeredToken;
        }

        // LAST, after every contributor and every substitution, and on psi.ArgumentList rather than the
        // local list — this is the vector the process receives, and nothing between here and Process.Start
        // touches it. Asserting the local list instead would certify something the OS never sees, which is
        // worse than no assertion because it looks like coverage.
        if (UsesMcpNameAllowlistArgv(descriptor) && psi.FileName != BorrowedReviewSandbox.SandboxExecPath)
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
    /// <summary>This daemon's own reviewer state directory — the same
    /// <c>{StateDir}/{name}</c> shape DaemonRunner uses for consent, so a reviewer home and a
    /// consent decision live under one owner-only root per daemon. Per DAEMON, never shared: the
    /// reviewer-home sweep's safety depends on every directory in its root belonging to this
    /// daemon.</summary>
    internal static string ReviewerStateDir(DaemonConfig config) => config.Store.StateDirectory(config.Name);

    internal static ReviewerVersionStore VersionStoreFor(DaemonConfig config, string vendor) =>
        new(ReviewerStateDir(config), vendor);

    /// <summary>The three inputs a gated reviewer's decision needs, resolved ONCE.</summary>
    /// <param name="Enabled">The operator's consent flag for this vendor.</param>
    /// <param name="Installed">The installed build, or null when unresolved — never probed for a
    /// disabled daemon, which is why the flag is read first.</param>
    /// <param name="Affirmed">The build this daemon recorded as accepted.</param>
    readonly record struct ReviewerGateInputs(bool Enabled, string? Installed, string? Affirmed);

    /// <summary>
    /// Resolves the gate's inputs with AT MOST ONE version probe, and none at all when the operator
    /// has not opted in — an installed-but-wedged binary must not stall a feature that is switched off.
    ///
    /// <para>Probing once matters on the refusal path too: the decision and the explanation both need
    /// the installed version, and resolving it per consumer spawned the vendor binary twice to produce
    /// one refusal.</para>
    /// </summary>
    static ReviewerGateInputs GateInputsFor(
            AcpVendorDescriptor descriptor, DaemonConfig config, bool enabled,
            Func<string, string?>? resolveVersion) {
        if (!enabled) return new(false, null, null);

        var installed = (resolveVersion ?? new VendorVersionResolver(config.Binaries).Resolve)(
            descriptor.ResolveBinaryPath(config));

        return new(true, installed, VersionStoreFor(config, descriptor.Vendor).Affirmed);
    }

    /// <summary>
    /// Why this daemon refuses <paramref name="descriptor"/>'s vendor as an unattended reviewer, or
    /// null when it does not. The ONE place the gate ladder is written — advertisement
    /// (<see cref="DescribeUnattendedSupport"/>), the launch boundary
    /// (<see cref="RequireReviewerCapability"/>) and the startup diagnostic all read it. They were two
    /// separately maintained ladders, which is how a vendor could be dropped from advertisement and
    /// thereby never reach the launch path that held the explanation.
    ///
    /// <para>Deliberately NOT cached: the launch boundary must re-judge a build swapped under a
    /// long-running daemon rather than read a startup snapshot.</para>
    /// </summary>
    internal static string? ReviewerRefusal(
            AcpVendorDescriptor descriptor, DaemonConfig config, Func<string, string?>? resolveVersion) {
        if (descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor) {
            var g    = GateInputsFor(descriptor, config, config.KiroUnattendedReviewerEnabled, resolveVersion);
            var kiro = KiroReviewerCapability.Decide(g.Enabled, g.Installed, g.Affirmed);

            return kiro == KiroReviewerDecision.Allowed
                ? null
                : KiroReviewerCapability.DenialReason(kiro, g.Installed, g.Affirmed);
        }

        if (descriptor.Vendor == AcpVendorDescriptors.OpenCode.Vendor) {
            var g   = GateInputsFor(descriptor, config, config.OpenCodeUnattendedReviewerEnabled, resolveVersion);
            var oc  = OpenCodeReviewerCapability.Decide(g.Enabled, g.Installed, g.Affirmed);

            return oc == OpenCodeReviewerDecision.Allowed
                ? null
                : OpenCodeReviewerCapability.DenialReason(oc, g.Installed, g.Affirmed);
        }

        if (!UsesMcpNameAllowlistArgv(descriptor)) return null;

        var gemini = GateInputsFor(descriptor, config, config.GeminiUnattendedReviewerEnabled, resolveVersion);
        var decision = GeminiReviewerCapability.Decide(gemini.Enabled, gemini.Installed, gemini.Affirmed);

        return decision == GeminiReviewerDecision.Allowed
            ? null
            : GeminiReviewerCapability.DenialReason(decision, gemini.Installed, gemini.Affirmed);
    }

    static void RequireReviewerCapability(
            AcpVendorDescriptor descriptor, DaemonConfig config, bool isReviewFlow,
            Func<string, string?>? resolveVersion) {
        if (!isReviewFlow) return;

        if (ReviewerRefusal(descriptor, config, resolveVersion) is { } refusal)
            throw new InvalidOperationException(refusal);
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
