using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// A turn process that can explain, after the fact, why it produced no protocol traffic. Kept off
/// <see cref="IAgyTurnProcess"/> — the runtime never needs it, only the launch path does, and a
/// runtime-visible diagnostics channel would invite exactly the kind of per-turn text accumulation
/// this deliberately bounds.
/// </summary>
internal interface IAgyTurnDiagnostics {
    /// <summary>A bounded capture of whatever the child wrote outside the NDJSON protocol (stderr,
    /// plus stdout lines that were not JSON at all), or null if it wrote nothing. Read once, on a
    /// failed launch, to turn "no <c>init</c> arrived" into a reason an operator can act on.</summary>
    string? Diagnostics { get; }
}

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Antigravity's CLI (<c>agy</c>) — both an unattended
/// review-flow reviewer and an interactive hosted agent — over the exec-per-turn
/// <see cref="AntigravityHostedAgentRuntime"/>.
///
/// <para><b>The two launch shapes it SERVES differ in exactly two things:</b> one argument
/// (<c>--dangerously-skip-permissions</c>, hosted only; see <see cref="BuildTurnPsi"/>) and the
/// injected MCP surface (the <c>kcap-flow-result</c> channel plus the flow definition's allowlist,
/// review only; see <see cref="BuildReviewFlowMcp"/>). In particular the per-launch isolated
/// <c>HOME</c> is NOT reviewer-only: this runtime is itself the transcript source, so a launch under
/// the operator's own home is captured a second time by the hook lane — isolation removes a duplicate
/// rather than removing capture.</para>
///
/// <para><b>The context carries a THIRD, which this factory refuses.</b> <c>LaunchKind.Review</c> (a
/// PR review, <c>ctx.IsReview</c>) is neither of the above and shares the hosted arm's
/// <c>!IsReviewFlow</c> predicate, so it is rejected at the top of <see cref="StartAsync"/> with
/// <c>antigravity_pr_review_unsupported</c> rather than served degraded — see the guard for why.</para>
///
/// <para><b>What this factory owns that the runtime deliberately does not:</b> the argv and
/// environment of every turn child, the per-launch isolated <c>HOME</c> (and its removal), the
/// absolute bound on the launch handshake, and the ordering the orchestrator depends on — that
/// <see cref="StartAsync"/> does not return until turn 1's <c>init</c> has resolved the conversation
/// id, because the orchestrator reads <c>transcript.AcpSessionId</c> synchronously the moment a
/// launch returns and would otherwise bind the transcript to <c>""</c> forever.</para>
///
/// <para><b>One gate ladder, parameterised by launch shape</b> (<see cref="LaunchRefusal"/>). Platform,
/// binary presence and the recorded build minimum gate BOTH shapes — they protect the isolated
/// <c>HOME</c> above, which is not reviewer-specific. Operator CONSENT
/// (<c>KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER</c>) gates only a review, because only a review is
/// cross-principal; hosted Antigravity ships on by default like the other hosted vendors. Advertisement
/// keeps the full ladder, consent included — advertising IS the offer to review unattended.</para>
///
/// <para><b>Auth is deliberately not an advertisement gate</b> (owner decision). The factory
/// advertises whenever consent is given and the binary resolves; an operator whose only auth is an
/// interactive <c>agy</c> login gets a bounded, coded launch failure naming the ADC remedy, rather
/// than a vendor that silently disappears from the list.</para>
///
/// <para><b>No borrowed lane</b> — no <c>sandbox-exec</c> substrate here, so
/// <see cref="AcpVendorDescriptor.SupportsBorrowedReviewFlow"/> stays false and a borrowed request fails closed to an
/// owned worktree, exactly as Kiro and Claude do.</para>
/// </summary>
/// <param name="turnSource">Test seam ONLY. Production passes null, which spawns the real
/// <see cref="AgyTurnProcess"/> from the <see cref="ProcessStartInfo"/> <see cref="BuildTurnPsi"/>
/// built — so the seam changes nothing about production behaviour, and the argv assertions run
/// against the same builder a real launch uses.</param>
/// <param name="binaryExists">Test seam ONLY, for the availability half of the gate. Production
/// passes null, which resolves the real binary through <c>PATH</c>. A test pins it so the CONSENT
/// half is assertable on a host that happens to have (or not have) <c>agy</c> installed — otherwise
/// the tests pass for the wrong reason on one machine and fail on another.</param>
/// <param name="resolveVersion">Test seam ONLY, for the installed-version half of the gate.
/// Production passes null, which spawns the configured binary to read its own reported version — the
/// same resolver every other gated reviewer uses. The MINIMUM it is compared against is never seamed:
/// it is a real record on disk, so a test moves it by writing one.</param>
/// <param name="posixHost">Test seam ONLY, for the platform half of the gate. Production passes null,
/// which reads the ambient OS.
///
/// <para><b>This exists because reading the ambient OS here made two arms unreachable on one
/// platform, and it is the second time this repository has paid for that.</b>
/// <see cref="AntigravityReviewerCapability.Decide"/> already takes the platform as a parameter,
/// and its own comment records why: an earlier Kiro revision called
/// <c>OperatingSystem.IsWindows()</c> inside a method claiming to be pure, and on the Windows CI leg
/// a dozen consent and version tests short-circuited to <c>UnsupportedPlatform</c> — failing for a
/// reason that had nothing to do with what they asserted. Collapsing this factory's ladder onto that
/// decision reintroduced the ambient read one layer up, and CI caught exactly two tests
/// (binary-missing, below-minimum) refusing on platform before reaching the arm under test. Taking the
/// platform as an argument makes every arm reachable from any host — including the Windows arm
/// itself, which is otherwise unassertable on POSIX.</para></param>
internal sealed partial class AntigravityHostedAgentRuntimeFactory(
        DaemonConfig                                                     config,
        ILoggerFactory                                                   loggerFactory,
        Func<ProcessStartInfo, CancellationToken, Task<IAgyTurnProcess>>? turnSource = null,
        Func<string, bool>?                                              binaryExists = null,
        Func<string, string?>?                                           resolveVersion = null,
        bool?                                                            posixHost = null
    ) : IHostedAgentRuntimeFactory {
    readonly ILogger _logger = loggerFactory.CreateLogger<AntigravityHostedAgentRuntimeFactory>();

    readonly Func<ProcessStartInfo, CancellationToken, Task<IAgyTurnProcess>> _turnSource =
        turnSource ?? ((psi, _) => Task.FromResult<IAgyTurnProcess>(
            new AgyTurnProcess(psi, loggerFactory.CreateLogger<AgyTurnProcess>())));

    readonly Func<string, bool> _binaryExists = binaryExists ?? CliResolver.Exists;

    readonly Func<string, string?> _resolveVersion = resolveVersion ?? VendorVersionResolver.Resolve;

    readonly bool _posixHost = posixHost ?? !OperatingSystem.IsWindows();

    /// <summary>The vendor token, never <c>agy</c>: the server routes on this exact string and the
    /// capture side already knows <c>antigravity</c>. <c>agy</c> is only ever a binary name.</summary>
    public string Vendor => "antigravity";

    public bool IsAvailable() => _binaryExists(config.AntigravityPath);

    public bool SupportsUnattended => DescribeUnattendedSupport().Supported;

    /// <summary>Every launch this runtime serves runs under the per-launch isolated <c>HOME</c>
    /// <see cref="StartAsync"/> creates, and an MCP server is spawned as the vendor's child, so the
    /// result channel inherits it and finds no token store. Unconditional rather than borrowed-scoped:
    /// this runtime refuses a borrowed workspace outright and redirects <c>HOME</c> regardless, which
    /// is exactly why a borrowed-ness predicate could not reach it.</summary>
    public bool ReviewFlowRedirectsHome => true;

    /// <summary>
    /// Advertisement keeps the FULL ladder, consent included, because advertising is specifically an
    /// offer to REVIEW unattended — the one thing the consent flag governs. A daemon that advertised
    /// without consent would be offering exactly what its operator never opted into, and the server
    /// refuses an unadvertised reviewer, so this is also the only seam that can withhold one.
    ///
    /// <para>Every refusal here carries a reason, because this vendor DOES offer unattended hosting:
    /// the <see langword="null"/> case in <see cref="UnattendedSupport"/> is for a vendor that never
    /// claimed it, which is not this one. The reason names the switch an operator can act on.</para>
    /// </summary>
    public UnattendedSupport DescribeUnattendedSupport() {
        var withheld = LaunchRefusal(reviewFlow: true);

        return new(withheld is null, withheld);
    }

    /// <summary>Why this daemon refuses to launch <c>agy</c> for a launch of the given shape, or null
    /// when it does not. The ONE place the ladder is written — advertisement and the launch boundary
    /// both read it, so a vendor cannot be dropped from advertisement and thereby never reach the path
    /// that holds the explanation, and the recorded MINIMUM cannot be enforced at only one of the two
    /// (an explicit <c>vendor: "antigravity"</c> request reaches a launch without consulting
    /// advertisement).
    ///
    /// <para><b>The two launch shapes differ in ONE arm, and it is a parameter rather than a second
    /// ladder</b> — two ladders that must agree is a shape this file has already paid for once.</para>
    ///
    /// <para>Every verdict and every text comes from <see cref="AntigravityReviewerCapability"/>; the
    /// only arm written here is the one that decision cannot express — a binary that does not resolve
    /// at all — placed BEFORE the probe so a daemon with no <c>agy</c> is told it is missing rather
    /// than that its version could not be read.</para>
    ///
    /// <para>Deliberately not cached: a long-running daemon must re-judge a binary installed (or
    /// removed) under it rather than read a startup snapshot — and, since the minimum is a record on
    /// disk rather than configuration, an affirmation taken while this daemon runs is picked up on the
    /// next decision.</para></summary>
    /// <param name="reviewFlow">Whether this launch is an unattended review, which is the ONLY shape
    /// the operator consent flag governs.
    ///
    /// <para><b>Consent is reviewer-only because its whole justification is cross-principal.</b> An
    /// unattended reviewer runs under the daemon user's authority and returns what it read to whoever
    /// requested the review — who need not be the operator. A hosted launch has no such exposure: the
    /// server's daemon registry is keyed <c>(TeamId, OwnerUserId, Name)</c> and both daemon discovery
    /// and the launch hub resolve a daemon with the CALLER's own normalized user id, so the launcher
    /// IS the daemon's owner. Hosted Antigravity therefore ships on by default, like the other hosted
    /// vendors, and a hosted launch on a consent-less daemon must not fail with a review complaint.
    /// </para>
    ///
    /// <para><b>Every OTHER arm applies to both shapes</b> — platform, binary presence and the
    /// recorded build minimum all exist to protect the per-launch isolated <c>HOME</c>, which a hosted
    /// launch depends on exactly as a review does.</para></param>
    internal string? LaunchRefusal(bool reviewFlow) {
        var posixHost = _posixHost;

        // The one parameterised arm. `true` for a hosted launch means "consent is not a question
        // here", so Decide's consent arm cannot fire and the ladder continues at platform.
        var consented = !reviewFlow || config.AntigravityUnattendedReviewerEnabled;

        // Consent and platform decided with NO probe and NO filesystem read. Decide short-circuits
        // both before it looks at a version, but C# evaluates arguments first — so reading the record
        // or probing agy inline here would do both for a daemon that switched the reviewer off, which
        // is exactly what the consent arm's short-circuit exists to prevent. A null installed version
        // can only yield those two arms or VersionUnresolved (the shared comparison checks the
        // installed side first), so that verdict is precisely "consent and platform passed", and the
        // ORDER between them stays owned by Decide rather than restated here.
        var beforeProbe = AntigravityReviewerCapability.Decide(
            posixHost, consented, installedVersion: null, minimumVersion: null);

        if (beforeProbe != AntigravityReviewerDecision.VersionUnresolved)
            return AntigravityReviewerCapability.DenialReason(
                beforeProbe, null, null, config.AntigravityPath);

        if (!IsAvailable())
            return $"antigravity_reviewer_binary_missing: '{config.AntigravityPath}' does not resolve to "
                 + "an executable. Install the Antigravity CLI (the `agy` binary — the IDE alone is not "
                 + "enough), or set KCAP_ANTIGRAVITY_PATH to its location.";

        // ONCE: the verdict and its explanation both need the version, and resolving it per consumer
        // spawns the vendor binary twice to produce one refusal.
        var installed = _resolveVersion(config.AntigravityPath);
        var minimum   = VersionStoreFor(config).Affirmed;

        var decision = AntigravityReviewerCapability.Decide(posixHost, consented, installed, minimum);

        return decision == AntigravityReviewerDecision.Allowed
            ? null
            : AntigravityReviewerCapability.DenialReason(
                decision, installed, minimum, config.AntigravityPath);
    }

    /// <summary>The daemon-owned record of the oldest <c>agy</c> build this daemon will run — the same
    /// shared store Kiro and Gemini use, keyed by vendor, under this daemon's own state root. Read
    /// through here rather than constructed at each site so the seeding path in <c>DaemonRunner</c>,
    /// the <c>affirm</c> verb and this gate cannot end up pointing at different files.</summary>
    internal static ReviewerVersionStore VersionStoreFor(DaemonConfig config) =>
        new(ReviewerStateDir(config), DaemonRunner.AntigravityVendor);

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        // A THIRD launch shape. Everything hosted below keys off !ctx.IsReviewFlow, which is true for
        // LaunchKind.Default AND LaunchKind.Review — so without this a PR-review launch takes the
        // hosted arm and runs with --dangerously-skip-permissions, an EMPTY MCP surface and no review
        // system prompt, because only PtyHostedAgentRuntimeFactory builds LauncherContext.ReviewLaunch
        // (the `kcap mcp review` config and that prompt). A PR-review agent silently missing every
        // review tool is worse than none; refuse it.
        //
        // FIRST, ahead of the containment ladder: no install, affirmation or consent can make this
        // shape work, so an operator should read that rather than a remedy that would not help.
        if (ctx.IsReview)
            throw new InvalidOperationException(
                "antigravity_pr_review_unsupported: this runtime hosts interactive agents and "
              + "review-flow reviewers only. A PR review needs the `kcap mcp review` tool surface and "
              + "review prompt, which only the PTY launchers build — launch the PR review with Claude.");

        // ONE ladder, told which shape of launch it is judging. Every arm but consent applies to both
        // — they protect the per-launch isolated home below, which a hosted launch relies on exactly
        // as a review does. Defence in depth for a review (the orchestrator's unattended gate runs
        // first, but an explicit `vendor: "antigravity"` request can reach a factory without
        // consulting advertisement), and the whole gate for a hosted launch, whose vendor is
        // advertised on binary presence alone.
        if (LaunchRefusal(ctx.IsReviewFlow) is { } refusal) throw new InvalidOperationException(refusal);

        // A property of THIS RUNTIME, not of reviews: there is no sandbox-exec substrate here, so
        // nothing bounds what a launch could read out of a checkout it does not own. Worded for either
        // shape — a borrowed request is review-only today, but the guard reads ctx.Work, not IsReviewFlow.
        if (ctx.Work != WorkLocation.OwnedWorktree)
            throw new InvalidOperationException(
                "antigravity_reviewer_requires_owned_worktree: this runtime has no containment "
              + "strategy for a borrowed workspace, so it runs only in a daemon-owned worktree.");

        // Canonical wire names: agy's MCP surface is the file this launch writes, not a name-matched
        // allowlist the reviewed repository could impersonate an entry in, so the per-launch aliasing
        // Gemini and Kiro need buys nothing here. Overwritten for BOTH shapes, so a caller-supplied
        // identity can never reach a launch of either kind.
        ctx = ctx with { LaunchIdentity = LaunchIdentity.ForLaunch(aliasResultChannel: false) };

        // Both the result channel AND its fail-closed validation are review-only — the same split
        // AcpHostedAgentRuntimeFactory.ValidateAndBuildReviewFlowMcp draws. A hosted agent has no flow
        // to report to, so injecting kcap-flow-result would hand it a tool it can only call
        // meaninglessly, and validating that channel's inputs would refuse a hosted launch for a
        // channel it never needed. The allowlist is the review-flow DEFINITION's, so its rejection is
        // review-only for the same reason.
        //
        // The hosted arm forwards ctx.McpServers, mirroring the ACP factory's hosted arm. No caller
        // populates that field today (see its comment on RuntimeStartContext), so a hosted launch's
        // surface is empty — deliberately, because nothing is offered, rather than by a drop here.
        var injected = ctx.IsReviewFlow ? BuildReviewFlowMcp(ctx) : ctx.McpServers ?? [];

        LogLaunching(ctx.AgentId, ctx.Worktree.Path, ctx.IsReviewFlow);

        // Created BEFORE any child exists, and owner-only from its first instant — the agent's own
        // conversation state lands in it. Its ABSENCE of a kcap plugin directory is what keeps capture
        // single-lane; the injected mcp_config.json is the agent's whole MCP surface.
        //
        // Kept for interactive launches too, and for the capture reason rather than a stdout one:
        // measured, a run under the operator's real HOME is recorded a SECOND time by the hook lane as
        // its own watcher session, while this runtime is already the transcript source. An inherited
        // HOME would duplicate capture, not add it.
        var stateDir = ReviewerStateDir(config);
        // The grant is review-only for the same reason the result channel is: a hosted launch already
        // runs with --dangerously-skip-permissions, so an allow-rule for it would be dead config, and
        // its injected servers are a caller's rather than a review definition's.
        var home     = AntigravityReviewerHome.Create(
            stateDir, config.DaemonEpoch ?? "unpinned", ctx.AgentId, injected,
            grantInjectedMcpTools: ctx.IsReviewFlow, _logger);

        // Created here rather than left to agy: TMPDIR must exist before the child writes into it, and
        // it is inside the home precisely so it is removed with it.
        Directory.CreateDirectory(TempDirIn(home));

        var model = ResolveModel(config, ctx);

        // Recorded so a failed launch can explain ITSELF — an unauthenticated agy produces no `init`,
        // and "the conversation id never arrived" is not a reason anyone can act on.
        IAgyTurnProcess? firstTurnProcess = null;

        async Task<IAgyTurnProcess> SpawnTurnAsync(string prompt, string? conversationId, CancellationToken turnCt) {
            var psi     = BuildTurnPsi(config, ctx, prompt, conversationId, home);
            var process = await _turnSource(psi, turnCt).ConfigureAwait(false);

            firstTurnProcess ??= process;

            return process;
        }

        var runtime = new AntigravityHostedAgentRuntime(
            spawnTurn: SpawnTurnAsync,
            logger: loggerFactory.CreateLogger<AntigravityHostedAgentRuntime>(),
            agentId: ctx.AgentId,
            model: model,
            cwd: ctx.Worktree.Path,
            launchDeadline: TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerLaunchTimeoutSeconds)),
            turnDeadline: TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerTurnTimeoutSeconds)),
            // Disposal, not disk hygiene: the home holds the reviewer's own conversation JSONL — the
            // caller's diff, source excerpts and findings. The daemon-epoch sweep is the crash
            // backstop, not the disposal path.
            onDisposed: () => AntigravityReviewerHome.Delete(home, stateDir, _logger));

        // MUST precede the first turn: a clock assigned later makes every stamp inside the launch a
        // silent no-op, and the reaper then judges this reviewer from an empty record.
        runtime.ActivityClock = ctx.ActivityClock;

        // ONE absolute budget across spawn → `init`, linked to the caller's token so a real shutdown
        // still wins and is never misreported as a timeout. This is what turns the measured
        // unauthenticated shapes — an immediate error, or an OAuth URL followed by a 60-second
        // interactive wait — into a bounded failure.
        using var launchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        launchDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerLaunchTimeoutSeconds)));

        try {
            await runtime.SendUserInputAsync(ctx.Prompt ?? "").ConfigureAwait(false);

            // The ordering guarantee. SendUserInputAsync returns as soon as the turn is enqueued, and
            // WaitForTurnIdleAsync is not a substitute (its enqueue→gate hand-off is itself async), so
            // this barrier is the only thing standing between the orchestrator and a transcript bound
            // to "".
            await runtime.WaitForConversationIdAsync(launchDeadline.Token).ConfigureAwait(false);
        } catch (Exception ex) {
            // The launch is over either way; leaving the child alive would leave an unreapable reviewer
            // holding a daemon slot, and leaving the home would leave review context on disk that
            // nobody downstream will ever dispose for us. DisposeAsync does both (it terminates, joins
            // the worker, then runs onDisposed).
            await runtime.DisposeAsync().ConfigureAwait(false);

            // A genuine shutdown propagates AS a cancellation. Dressing it up as a launch failure
            // would put a coded reviewer error in the record for every agent in flight when the
            // daemon stopped, and send an operator looking for a fault that never happened.
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;

            throw DescribeLaunchFailure(ex, firstTurnProcess, ct, launchDeadline.Token);
        }

        // The runtime IS the transcript source, so the orchestrator binds and forwards without
        // downcasting Runtime. McpConfigPath stays null deliberately: this launch's mcp config lives
        // INSIDE the home, and the home's own disposal owns it — reporting it separately would have
        // the orchestrator delete a file out of a directory it knows nothing about.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>The reviewer's injected MCP surface — the <c>kcap-flow-result</c> submit channel plus
    /// the flow definition's allowlist — or a throw naming what makes it undeliverable. Called for a
    /// review flow ONLY: every refusal in here describes a review, and a hosted launch that hit one
    /// would be refused for machinery it has no use for.
    ///
    /// <para>Fails closed rather than launching a reviewer with a missing or partial channel: such a
    /// reviewer starts, runs and can never report, so the flow waits for a verdict that cannot
    /// arrive.</para></summary>
    static IReadOnlyList<AcpMcpServerSpec> BuildReviewFlowMcp(RuntimeStartContext ctx) {
        // A blank agent id would still yield a non-empty server list and slip past a count-only guard,
        // so all three result-channel inputs are checked — a dead channel wedges the round.
        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath)
         || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "antigravity_reviewer_result_channel_incomplete: cannot inject the kcap-flow-result "
              + "channel (missing server url / kcap path / agent id).");

        // This runtime DECLARES that its reviews redirect HOME (see ReviewFlowRedirectsHome), so a
        // review reaching here without brokered delivery means that declaration was not honoured —
        // the same code-level-invariant shape as AcpHostedAgentRuntimeFactory.ValidateBorrowedArtifact.
        // The alternative to failing is a reviewer that reads its context, reasons, and can never
        // report: the round then burns its whole timeout with no error anywhere.
        if (!ctx.RequiresBrokeredResultDelivery)
            throw new InvalidOperationException(
                "antigravity_reviewer_result_delivery_unbrokered: this reviewer runs under a "
              + "per-launch isolated HOME, so its result channel has no token store to authenticate "
              + "from and must be launched with a daemon-brokered delivery capability.");

        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var allowlistServerIds, out var rejected))
            throw new InvalidOperationException(
                $"antigravity_reviewer_mcp_allowlist_rejected: '{rejected}' is not an auto-approvable "
              + "read-only server.");

        return AcpReviewFlowMcp.Build(ctx, allowlistServerIds);
    }

    /// <summary>
    /// Turns a failed handshake into a reason an operator can act on. The auth arm is
    /// <b>non-retryable and names the ADC remedy</b> — a generic launch failure would send an operator
    /// looking at the daemon, the flow or the network, when the actual fix is three environment
    /// variables.
    ///
    /// <para><b>GOOGLE_APPLICATION_CREDENTIALS is one of the three, and naming it is load-bearing.</b>
    /// ADC's default location is <c>$HOME/.config/gcloud/application_default_credentials.json</c>, and
    /// this launch redirects <c>HOME</c> to a per-launch state directory — so the credential
    /// <c>gcloud auth application-default login</c> just wrote is invisible to the child, and neither
    /// <c>AGY_ADC_AUTH</c> nor <c>GOOGLE_CLOUD_PROJECT</c> carries a path that would find it. An earlier
    /// revision of this message named only those two; measured, an operator following it exactly still
    /// gets <c>authentication required. Run 'agy' to log in.</c> The remedy text has to be sufficient on
    /// its own — a remedy that leaves the operator where they started reads as a broken feature.</para>
    ///
    /// <para>The daemon deliberately does NOT synthesize the path from its own <c>HOME</c>. Per the
    /// borrowed-review auth design it never goes <i>looking</i> for a credential — it forwards what the
    /// operator exported and nothing else — and reading a well-known credential location is exactly
    /// that. <c>ServiceEnvironment</c> already carries this key into a supervised unit off-Windows, so
    /// the export survives a service install without the daemon ever reading the file.</para>
    /// </summary>
    static InvalidOperationException DescribeLaunchFailure(
            Exception cause, IAgyTurnProcess? firstTurn, CancellationToken callerToken, CancellationToken launchToken) {
        if (firstTurn is IAgyTurnDiagnostics { Diagnostics: { } diagnostics } && LooksLikeAuthFailure(diagnostics))
            return new InvalidOperationException(
                "antigravity_reviewer_auth_unavailable: agy could not authenticate, and a daemon-hosted "
              + "agy has no way to complete an interactive login (its stdin is closed). Give the "
              + "daemon durable credentials: `gcloud auth application-default login`, then set ALL THREE "
              + "of GOOGLE_CLOUD_PROJECT=<project>, AGY_ADC_AUTH=1 and "
              + "GOOGLE_APPLICATION_CREDENTIALS=<absolute path to "
              + "application_default_credentials.json> in the daemon's environment. The path is required "
              + "even though ADC has a default location: a reviewer launch redirects HOME, so the "
              + "default location is not visible to it. A supervised daemon installed before these were "
              + "exported must be reinstalled to capture them.",
                cause);

        if (launchToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            return new InvalidOperationException(
                "antigravity_reviewer_launch_timeout: agy did not report a conversation within the "
              + "launch deadline. The child was terminated and its isolated home removed. An "
              + "unauthenticated agy can sit on an interactive login prompt rather than failing, which "
              + "is the shape this bound exists for.",
                cause);

        return new InvalidOperationException(
            "antigravity_reviewer_launch_failed: agy's first turn ended without reporting a "
          + "conversation id, so there is no session to bind a transcript to.",
            cause);
    }

    /// <summary>
    /// Whether a child's non-protocol output is the authentication signal. Substring, deliberately
    /// loose, and never the sole basis for anything destructive: the two measured shapes are an
    /// immediate <c>authentication required</c> error and an OAuth URL followed by an interactive
    /// wait, and both only ever change which REASON a launch that already failed reports.
    /// </summary>
    internal static bool LooksLikeAuthFailure(string? diagnostics) =>
        diagnostics is { Length: > 0 } text
     && (text.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
      || text.Contains("log in", StringComparison.OrdinalIgnoreCase)
      || text.Contains("oauth", StringComparison.OrdinalIgnoreCase));

    /// <summary>The effective reviewer model — the launch's own override, else the daemon-wide
    /// default. ONE derivation, read by both the argv builder and the runtime's
    /// <c>ResolvedModel</c>, so the model we report can never differ from the model we passed.
    /// Mirrors the <c>"default"</c> sentinel convention the rest of the daemon already uses for
    /// "no override requested".</summary>
    internal static string? ResolveModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.AntigravityModel;

    /// <summary>This daemon's own reviewer state directory — the same <c>{StateDir}/{name}</c> shape
    /// the ACP reviewers use, so a reviewer home and a consent decision live under one owner-only root
    /// per daemon. Per DAEMON, never shared: the home sweep's safety depends on every directory in its
    /// root belonging to this daemon.</summary>
    internal static string ReviewerStateDir(DaemonConfig config) =>
        Path.Combine(config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));

    internal static string TempDirIn(string home) => Path.Combine(home, "tmp");

    /// <summary>
    /// PURE builder for ONE turn's spawn shape — no process, no filesystem side effects. The real
    /// spawn path and the launch tests both go through it, so an assertion here certifies the vector
    /// the OS actually receives rather than a re-derivation of it.
    ///
    /// <para><b>The one asymmetry between a hosted agent and a reviewer</b> is
    /// <c>--dangerously-skip-permissions</c>, added on the hosted arm only. A hosted agent exists to DO
    /// work, and without it agy's shell and out-of-workspace operations soft-deny while the run still
    /// exits 0 — so the agent merely looks broken. <b>That flag is also the read boundary, and the
    /// worktree is not:</b> measured on agy 1.1.10, with it an absolute <c>view_file</c> of a path
    /// OUTSIDE the workspace succeeds, and without it the same read is refused with a typed
    /// <c>tool_info.error</c>. Nothing here should be read as the daemon-owned worktree confining what
    /// a hosted agent can see.</para>
    ///
    /// <para><b>Claude is a precedent for a single-axis no-prompt posture existing, NOT for which
    /// launch kind gets it.</b> Codex is not the analogue — its posture is two axes
    /// (<c>Sandbox</c> × <c>Approval</c>), so a no-prompt Codex still sits on a sandbox. But Claude's
    /// own split runs the OTHER way: <c>ClaudeLauncher.BuildArgs</c> adds
    /// <c>bypassPermissions</c> only under <c>ctx.IsReviewFlow</c>, because its reviewer writes into a
    /// throwaway worktree while its interactive agent has a human to answer the dialog. This runtime
    /// has neither property. Do not "align with the precedent" by moving this flag to the reviewer
    /// arm — that is the direction the split exists to prevent.</para>
    ///
    /// <para><b>What is deliberately absent.</b> No <c>--dangerously-skip-permissions</c> for a
    /// reviewer: it runs in a daemon-OWNED worktree and needs only to read it, which agy's headless
    /// defaults already permit — its soft-deny of shell and out-of-workspace operations IS the desired
    /// unattended posture, and widening it would grant a reviewer shell access it has no reason to
    /// hold. No <c>--sandbox</c> on either arm: a vendor-side terminal restriction overlapping what
    /// containment already provides, and unprobed.</para>
    ///
    /// <para><c>--print-timeout</c> is passed on EVERY invocation rather than relying on agy's own
    /// <c>5m0s</c> default — a vendor change would otherwise silently move a bound we did not
    /// choose.</para>
    /// </summary>
    internal static ProcessStartInfo BuildTurnPsi(
            DaemonConfig config, RuntimeStartContext ctx, string prompt, string? conversationId, string home) {
        var argv = new List<string> {
            "-p", prompt,
            "--output-format", "stream-json",
            "--disable-slash-commands"
        };

        // Hosted only, never a reviewer — see this method's doc for what the flag does and does not
        // bound. Passed unconditionally on that arm: no caller input selects it.
        if (!ctx.IsReviewFlow) argv.Add("--dangerously-skip-permissions");

        argv.Add("--print-timeout");
        argv.Add($"{Math.Max(1, config.AntigravityReviewerTurnTimeoutSeconds)}s");

        // Absent on turn 1 (there is nothing to resume yet) and present on every turn after it — this
        // is what makes a multi-round review land as ONE conversation rather than one per round.
        if (!string.IsNullOrEmpty(conversationId)) {
            argv.Add("--conversation");
            argv.Add(conversationId);
        }

        // An unknown slug makes agy hard-fail, which is a clean audit signal rather than a silent
        // downgrade to whatever it would have picked.
        if (ResolveModel(config, ctx) is { Length: > 0 } model) {
            argv.Add("--model");
            argv.Add(model);
        }

        var psi = new ProcessStartInfo(config.AntigravityPath, argv) {
            WorkingDirectory = ctx.Worktree.Path,
            // Redirected and then CLOSED at spawn (see AgyTurnProcess): nothing can consume a pasted
            // OAuth code, so an unauthenticated agy fails against the launch deadline instead of
            // waiting on a human who is not there.
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // Also what makes the daemon's own Google auth variables reach the child by inheritance —
            // they are deliberately not re-stamped below, so a rotated ADC path is read at spawn.
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        // Containment: the whole HOME is the per-launch isolated one, and TMPDIR sits inside it so
        // agy's temp writes are removed with the home rather than left in the operator's tree.
        psi.Environment["HOME"]   = home;
        psi.Environment["TMPDIR"] = TempDirIn(home);

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) psi.Environment["KCAP_URL"] = ctx.ServerUrl;

        // Without these a surviving turn child is invisible to OrphanReaper's env-marker pass — and
        // nothing fails visibly when they are omitted, which is exactly why they are stamped here
        // rather than left to the runtime.
        psi.Environment["KCAP_AGENT_ID"] = ctx.AgentId;
        if (!string.IsNullOrEmpty(ctx.DaemonId))    psi.Environment["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Antigravity launch: agentId={AgentId} cwd={Cwd} reviewFlow={ReviewFlow}")]
    partial void LogLaunching(string agentId, string cwd, bool reviewFlow);
}

/// <summary>
/// <see cref="IAgyTurnProcess"/> over a real <see cref="Process"/> — ONE <c>agy -p</c> turn. Mirrors
/// <see cref="AcpChildProcess"/>'s terminate/wait semantics (SIGTERM-then-kill via
/// <see cref="Process.Kill(bool)"/>, bounded waits that return silently on timeout), and honours the
/// two contracts <see cref="IAgyTurnProcess"/> states: <see cref="DisposeAsync"/> is idempotent, and
/// <see cref="TerminateAsync"/> is safe after it — the runtime can reach both on the same instance
/// microseconds apart when a stop races a turn's own unwinding.
///
/// <para><b>Identity is captured at construction</b>, not read on demand: <see cref="Process.Id"/>
/// throws once the process object is disposed, and the PID is exactly what an orphan reaper needs
/// most after the fact.</para>
/// </summary>
internal sealed partial class AgyTurnProcess : IAgyTurnProcess, IAgyTurnDiagnostics {
    /// <summary>Enough to carry an auth error and the URL under it, and small enough that a chatty
    /// child cannot grow the daemon's heap one turn at a time. Over-cap output is dropped, never
    /// rotated — this exists to explain a FAILED launch, and that explanation is at the start.</summary>
    const int DiagnosticsCap = 4096;

    readonly Process                 _process;
    readonly ILogger                 _logger;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;
    readonly Lock                    _diagnosticsGate = new();
    readonly StringBuilder           _diagnostics     = new();

    int _disposed;

    internal AgyTurnProcess(ProcessStartInfo psi, ILogger logger)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"antigravity_turn_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger) { }

    internal AgyTurnProcess(Process process, ILogger logger) {
        _process = process;
        _logger  = logger;
        Pid      = SafePid(process);

        // Closed immediately, and this is a containment decision rather than tidiness: an
        // unauthenticated agy prints an OAuth URL and waits for a pasted code, and a child with no
        // readable stdin cannot be handed one. The launch deadline then bounds it.
        try {
            _process.StandardInput.Close();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Antigravity: could not close a turn child's stdin.");
        }

        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    public int  Pid       { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    /// <summary>
    /// This turn's stdout NDJSON, line by line, ending at EOF. Lines that are not JSON at all are
    /// still yielded (the runtime's parser drops them) AND snooped into the diagnostics buffer:
    /// measured, agy reports "authentication required" as plain text on this stream, so a
    /// stderr-only capture would miss the one failure this whole bound exists for.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (true) {
            string? line;

            try {
                line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            } catch (IOException) {
                break;   // pipe torn down (the child was killed mid-read) — EOF, per this method's contract
            } catch (ObjectDisposedException) {
                break;   // stream disposed under us by a racing DisposeAsync — likewise EOF
            }

            if (line is null) break;

            if (!line.StartsWith('{')) Capture(line);

            yield return line;
        }
    }

    /// <summary>Keeps the stderr pipe drained — an undrained one fills at ~64KB and blocks the child
    /// on its next write, which for this runtime would wedge a turn forever. Never logs the text
    /// itself: agy's stderr can carry prompt fragments, paths and, on the auth path, a URL bearing a
    /// login code.</summary>
    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break;      // EOF — the child exited and closed the stream
                if (line.Length == 0) continue;

                Capture(line);
                LogStderrShape(line.Length);
            }
        } catch (OperationCanceledException) {
            // Disposal asked the drain to stop — expected.
        } catch (IOException) {
            // Pipe torn down on teardown — expected.
        } catch (ObjectDisposedException) {
            // Stream disposed out from under the read — expected.
        }
    }

    void Capture(string line) {
        lock (_diagnosticsGate) {
            if (_diagnostics.Length >= DiagnosticsCap) return;

            _diagnostics.Append(line).Append('\n');
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Timed out — return silently, per this method's contract.
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Already exited or disposed — nothing left to wait for.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            _process.Kill(entireProcessTree: true);
        } catch {
            // Already exited, already disposed (the contract explicitly permits this call after
            // DisposeAsync), or the kill raced the exit — nothing left to terminate either way.
            return;
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // idempotent, per the interface contract

        // No bounded wait after the kill, deliberately. Kill(entireProcessTree: true) is SIGKILL on
        // POSIX, which no child can catch or defer, so the death is already effectively synchronous
        // with the call — a wait here was measured to change nothing observable against a real child.
        // Callers that need a CONFIRMED exit terminate first and read HasExited while the handle is
        // still valid (see AntigravityHostedAgentRuntime.ProcessTurnAsync's teardown), which is the
        // only place that reading is truthful anyway.
        try {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        } catch {
            // Best-effort — already exited or inaccessible.
        }

        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
            // Best-effort.
        }

        try {
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch {
            // DrainStderrAsync already swallows its expected exceptions; never let a stuck drain hang
            // or fault a dispose.
        }

        _stderrDrainCts.Dispose();

        try {
            _process.Dispose();
        } catch {
            // Best-effort.
        }
    }

    static int SafePid(Process process) {
        try {
            return process.Id;
        } catch {
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Antigravity turn stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
