using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Gemini;

/// <summary>
/// The Gemini reviewer's launch invariants, asserted on the LAUNCH ARTIFACT — the argv the process actually
/// receives — rather than on descriptor fields or helper return values. A descriptor test proves what a
/// constant says; only the built launch proves what Gemini is told.
///
/// <para>Two failures these guard, both silent: a reviewer whose allowlist does not admit its own result
/// channel (it starts normally and can never report), and a reviewer launched with approval prompting
/// restored (it stalls on a permission frame no human answers).</para>
/// </summary>
public class GeminiReviewerLaunchTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static readonly Guid ChannelGuid   = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    static readonly Guid DenyGuid      = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    static readonly Guid AllowlistGuid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    static LaunchIdentity Identity =>
        LaunchIdentity.FromGuids(ChannelGuid, DenyGuid, AllowlistGuid, aliasResultChannel: true);

    /// <summary>A daemon that has opted in, on a certified vendor build — the only combination that launches.</summary>
    /// <summary>Enabled AND carrying an affirmation for the build these launches report, seeded exactly
    /// as enabling the reviewer does in production. Without the affirmation every launch is refused over
    /// an upgrade that never happened.</summary>
    // EnabledConfig is a property, not a field: every read takes a fresh daemon name, and so a fresh
    // per-name state root, so one test's version affirmation can never leak into another's.
    DaemonConfig EnabledConfig {
        get {
            var config = new DaemonConfig {
                GeminiUnattendedReviewerEnabled = true,
                Store = Daemons.Store,
                Name  = "test-daemon-" + Guid.NewGuid().ToString("N")
            };

            AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.Gemini.Vendor)
                .Affirm(CertifiedVersion);

            return config;
        }
    }

    /// <summary>The build these launches run against. Under the affirmation model any build works
    /// provided the daemon has affirmed it, so this is just a fixed value the config below affirms —
    /// it is no longer coupled to a maintainer-curated list.</summary>
    const string CertifiedVersion = "0.54.0";

    static RuntimeStartContext Ctx(bool isReviewFlow, string[]? mcpAllowlist = null) => new RuntimeStartContext(
        AgentId: "agent-1", Vendor: "gemini", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: isReviewFlow ? "http://kcap.test" : null,
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap")
        with { LaunchIdentity = Identity, McpAllowlist = mcpAllowlist };

    string[] Build(bool isReviewFlow, DaemonConfig? config = null, string? version = null,
                          string[]? mcpAllowlist = null) =>
        [.. AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Gemini, config ?? EnabledConfig, Ctx(isReviewFlow, mcpAllowlist),
                resolveGeminiVersion: _ => version ?? CertifiedVersion)
            .ArgumentList];

    // ── the whole vector, both launch kinds ──

    /// <summary>
    /// The review launch, compared in full. A whole-vector assertion fails on any unexpected token whatever
    /// its spelling, which is why it replaced three attempts at scanning for dangerous options: the vendor's
    /// parser runs with camel-case expansion and boolean negation, so an enumerated key list cannot be
    /// proven complete.
    /// </summary>
    [Test]
    public async Task ReviewLaunch_ArgvIsExactlyTheCanonicalVector() {
        // Joined rather than IsEquivalentTo: that assertion ignores ORDER, which is exactly what a
        // whole-vector guard must not do — `--allowed-mcp-server-names` and its value being adjacent is the
        // property. Joining also makes a failure readable as the command line it is.
        await Assert.That(string.Join(" ", Build(isReviewFlow: true))).IsEqualTo(
            "--experimental-acp --skip-trust "
          + $"--allowed-mcp-server-names {Identity.ResultChannelWireName} "
          + "--approval-mode yolo");
    }

    /// <summary>
    /// The interactive launch, unchanged by this work: the deny-all name, and NO approval mode — a hosted
    /// session must behave as the user's own does, so blanket approval must never leak onto it.
    /// </summary>
    [Test]
    public async Task InteractiveLaunch_ArgvIsExactlyTheCanonicalVector() {
        await Assert.That(string.Join(" ", Build(isReviewFlow: false))).IsEqualTo(
            "--experimental-acp --skip-trust "
          + $"--allowed-mcp-server-names {Identity.UnmatchableMcpName}");
    }

    // ── the coupling the descriptor test could not assert ──

    /// <summary>
    /// THE assertion. The allowlist value and the injected result channel's name must be the same string, or
    /// the reviewer launches and can never report — the failure mode with no symptom until a round times out.
    /// Compares the argv against the MCP list the same launch built, not two reads of one property.
    /// </summary>
    [Test]
    public async Task ReviewLaunch_AllowlistNamesExactlyTheInjectedResultChannel() {
        // ONE context, so this proves the two consumers agree on the SAME identity instance rather than two
        // fixtures that happen to hold equal GUIDs. Review caught the earlier version building each from its
        // own Ctx() call: it stayed green even if production handed a different identity to each consumer,
        // which is exactly the silent failure the type exists to prevent.
        var ctx = Ctx(isReviewFlow: true);

        var argv = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Gemini, EnabledConfig, ctx,
                resolveGeminiVersion: _ => CertifiedVersion)
            .ArgumentList;
        var injected = AcpReviewFlowMcp.Build(ctx, []);

        var allowed = argv[argv.IndexOf("--allowed-mcp-server-names") + 1];

        await Assert.That(injected.Select(s => s.Name)).Contains(allowed);
        await Assert.That(allowed).IsEqualTo(ctx.LaunchIdentity!.ResultChannelWireName);
    }

    /// <summary>The allowlist is exactly one OPTION occurrence (a second occurrence widens the gate — the
    /// option is array-typed) with one non-empty value (an EMPTY one disables the gate rather than denying
    /// all). With nothing extra injected the value is a single name, no comma.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Launch_CarriesExactlyOneAllowlistOptionAndOneNonEmptyValue(bool isReviewFlow) {
        var argv = Build(isReviewFlow);

        await Assert.That(argv.Count(a => a == "--allowed-mcp-server-names")).IsEqualTo(1);

        var value = argv[Array.IndexOf(argv, "--allowed-mcp-server-names") + 1];
        await Assert.That(value).IsNotEmpty();
        await Assert.That(value).DoesNotContain(",");
    }

    // ── allowlist servers: the gate widens to exactly the injected set, under aliased names ──

    /// <summary>
    /// A review launch with a definition MCP allowlist still carries ONE option occurrence; the extra
    /// servers ride the same value comma-joined (the option is comma-coerced — measured on 0.53.0: both
    /// admitted servers spawn and reach tools/call, an injected name outside the gate never spawns).
    /// </summary>
    [Test]
    public async Task ReviewLaunchWithAllowlist_StillCarriesExactlyOneAllowlistOption() {
        var argv = Build(isReviewFlow: true, mcpAllowlist: ["kcap-review"]);

        await Assert.That(argv.Count(a => a == "--allowed-mcp-server-names")).IsEqualTo(1);
        await Assert.That(argv[Array.IndexOf(argv, "--allowed-mcp-server-names") + 1])
            .IsEqualTo($"{Identity.ResultChannelWireName},{Identity.AllowlistWireName("kcap-review")}");
    }

    /// <summary>
    /// THE parity assertion for the widened gate: the comma-split gate value must equal — same names, same
    /// order — the server list the SAME launch context builds for <c>session/new</c>. A gate admitting fewer
    /// names than the injection ships blocked servers (the original defect: injected but silently excluded);
    /// a gate admitting more opens the exact-name allowlist beyond what this launch runs.
    /// </summary>
    [Test]
    public async Task ReviewLaunchWithAllowlist_GateNamesExactlyTheInjectedServerSet() {
        var ctx = Ctx(isReviewFlow: true, mcpAllowlist: ["kcap-review", "kcap-sessions"]);

        var argv = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Gemini, EnabledConfig, ctx,
                resolveGeminiVersion: _ => CertifiedVersion)
            .ArgumentList;
        var injected = AcpReviewFlowMcp.Build(ctx, ["kcap-review", "kcap-sessions"]);

        var gate = argv[argv.IndexOf("--allowed-mcp-server-names") + 1];

        await Assert.That(gate.Split(',').SequenceEqual(injected.Select(s => s.Name))).IsTrue();
    }

    /// <summary>
    /// The gate must never admit a CANONICAL allowlist id: it is a fixed public literal the reviewed
    /// repository can declare its own <c>.gemini/settings.json</c> server under, and the vendor's gate is an
    /// exact-name match — admitting it would spawn that repo-authored process as the daemon user, the same
    /// impersonation shape the result channel's per-launch alias closes (spec §2.3/§2.6).
    /// </summary>
    [Test]
    public async Task ReviewLaunchWithAllowlist_AdmitsAliasedNames_NeverTheCanonicalId() {
        var argv = Build(isReviewFlow: true, mcpAllowlist: ["kcap-review"]);
        var gate = argv[Array.IndexOf(argv, "--allowed-mcp-server-names") + 1].Split(',');

        await Assert.That(gate).DoesNotContain("kcap-review");
        await Assert.That(gate).Contains(Identity.AllowlistWireName("kcap-review"));
    }

    /// <summary>
    /// Same guarantee on the direct-builder path with NO caller-supplied identity (review finding): the
    /// builder's fallback identity must also be what the MCP-list builder reads, or the gate is computed
    /// from a null identity's canonical fallbacks — repository-matchable names — while the argv
    /// substitution uses the fresh identity. The checked value must BE the used value.
    /// </summary>
    [Test]
    public async Task ReviewLaunchWithoutACallerIdentity_StillGatesOnAliasedNames_NeverCanonical() {
        var ctx = Ctx(isReviewFlow: true, mcpAllowlist: ["kcap-review"]) with { LaunchIdentity = null };

        var argv = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Gemini, EnabledConfig, ctx,
                resolveGeminiVersion: _ => CertifiedVersion)
            .ArgumentList;

        var gate = argv[argv.IndexOf("--allowed-mcp-server-names") + 1].Split(',');

        await Assert.That(gate).DoesNotContain(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(gate).DoesNotContain("kcap-review");
    }

    /// <summary>Neither the placeholder nor a deny-all name may survive into a review launch — the review arm
    /// REPLACES the value, and appending instead would leave the gate open on the deny entry.</summary>
    [Test]
    public async Task ReviewLaunch_CarriesNeitherThePlaceholderNorTheDenyAllName() {
        var argv = Build(isReviewFlow: true);

        await Assert.That(argv).DoesNotContain(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder);
        await Assert.That(argv).DoesNotContain(Identity.UnmatchableMcpName);
    }

    /// <summary>And the placeholder is never emitted on any launch — it is a template marker, not a name.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task NoLaunchEmitsThePlaceholder(bool isReviewFlow) {
        await Assert.That(Build(isReviewFlow))
            .DoesNotContain(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder);
    }

    // ── approval mode ──

    [Test]
    public async Task InteractiveLaunch_CarriesNoApprovalModeInAnySpelling() {
        var argv = Build(isReviewFlow: false);

        foreach (var token in argv)
            await Assert.That(token.StartsWith("--approval-mode", StringComparison.OrdinalIgnoreCase)
                           || token.Equals("--yolo", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task NoLaunchCarriesTheLegacyYoloFlag() {
        await Assert.That(Build(isReviewFlow: true)).DoesNotContain("--yolo");
        await Assert.That(Build(isReviewFlow: false)).DoesNotContain("--yolo");
    }

    // ── the operator capability gate: the boundary, not the advertisement ──

    /// <summary>
    /// A disabled daemon refuses in the PURE BUILDER, which is what <c>StartRealProcess</c> calls immediately
    /// before <c>Process.Start</c> — so an explicit <c>vendor: "gemini"</c> request cannot reach a spawn by
    /// routing around the advertisement check.
    /// </summary>
    [Test]
    public async Task AnExplicitlyDisabledDaemon_RefusesAReviewLaunchBeforeAnyProcessCouldStart() {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(isReviewFlow: true,
                        config: new DaemonConfig { GeminiUnattendedReviewerEnabled = false }));

        await Assert.That(ex!.Message).Contains("gemini_unattended_reviewer_disabled");
    }

    /// <summary>A build OLDER than the recorded minimum — or one that cannot be identified at all — is
    /// refused even when the operator has opted in: the reviewer's only containment is that build's
    /// MCP-allowlist semantics, so a build we cannot vouch for is refused rather than assumed compatible.
    ///
    /// <para>`0.55.0` was in this list until the recorded version became a MINIMUM; a build newer than
    /// the minimum is now admitted, and <see cref="ANewerBuildThanTheMinimum_LaunchesUnchanged"/> pins
    /// that direction instead.</para></summary>
    [Test]
    [Arguments("0.53.1")]
    [Arguments("")]
    public async Task ABuildBelowTheMinimumOrUnresolvable_RefusesAReviewLaunch(string version) {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(isReviewFlow: true, version: version));

        await Assert.That(ex!.Message).Contains("gemini_reviewer_version");
    }

    /// <summary>The positive control the case above needs: the SAME wiring permits the launch once the
    /// installed build is the affirmed one. Without it, a gate that refused everything would pass.</summary>
    [Test]
    public async Task TheAffirmedBuild_PermitsAReviewLaunch() {
        var argv = Build(isReviewFlow: true, version: CertifiedVersion);

        await Assert.That(argv).Contains("--experimental-acp");
    }

    /// <summary>
    /// The direction the minimum exists to allow: a build NEWER than the recorded one launches with no
    /// operator action. `0.55.0` used to be an argument to the refusal case above — a vendor patch
    /// release took the reviewer offline until someone re-affirmed, which is the treadmill removed here.
    /// </summary>
    [Test]
    [Arguments("0.55.0")]
    [Arguments("1.0.0")]
    public async Task ANewerBuildThanTheMinimum_LaunchesUnchanged(string version) {
        var argv = Build(isReviewFlow: true, version: version);

        await Assert.That(argv).Contains("--experimental-acp");
    }

    /// <summary>
    /// The gate is review-only. An interactive hosted Gemini has always worked on any installed build and
    /// must keep working on a daemon that never opted into the reviewer — otherwise this change would take
    /// hosting offline as a side effect.
    /// </summary>
    [Test]
    [Arguments("0.54.0")]
    [Arguments("")]
    public async Task ADisabledDaemonOnAnyVersion_StillBuildsAnInteractiveLaunch(string version) {
        var argv = Build(isReviewFlow: false, config: new DaemonConfig(), version: version);

        await Assert.That(argv).Contains("--experimental-acp");
        await Assert.That(argv).Contains(Identity.UnmatchableMcpName);
    }

    // ── other vendors are untouched ──

    /// <summary>
    /// Cursor's launch must be byte-identical to before the alias existed: its result channel keeps the
    /// canonical name, and it gains no allowlist option and no approval mode. Aliasing being opt-in per
    /// vendor is a regression guard, not a nicety.
    /// </summary>
    [Test]
    public async Task CursorReviewLaunch_IsUnaffectedByTheGeminiAlias() {
        var ctx = Ctx(isReviewFlow: true) with {
            Vendor         = "cursor",
            LaunchIdentity = LaunchIdentity.FromGuids(ChannelGuid, DenyGuid, AllowlistGuid, aliasResultChannel: false)
        };

        var argv = AcpHostedAgentRuntimeFactory
            .BuildProcessStartInfo(AcpVendorDescriptors.Cursor, new DaemonConfig(), ctx)
            .ArgumentList;

        await Assert.That(argv).DoesNotContain("--allowed-mcp-server-names");
        await Assert.That(argv).DoesNotContain("--approval-mode");

        var injected = AcpReviewFlowMcp.Build(ctx, ["kcap-review"]);
        await Assert.That(injected.Select(s => s.Name)).Contains(KcapMcpRegistry.ReservedResultChannelId);
        // Allowlist servers keep their CANONICAL ids for a non-aliasing vendor — Cursor has no name gate,
        // and renaming its servers as a side effect of a Gemini change is exactly what this guard exists for.
        await Assert.That(injected.Select(s => s.Name)).Contains("kcap-review");
    }

    // ── the whole-vector assertion itself ──
    //
    // Mutation testing found the assertion's CALL SITE could be deleted with nothing going red: it is
    // unfalsifiable through the real path today, because nothing untrusted reaches the argv (the launch
    // context carries no arguments field and the only contributors are two descriptor constants). That is
    // the premise the design rests on — but an uncovered guard proves nothing, and the guard's whole purpose
    // is to catch a FUTURE contributor. So the future is simulated: a synthetic Gemini-vendor descriptor
    // carrying an extra argv token is exactly what a fourth contributor would produce.

    static AcpVendorDescriptor GeminiWithExtraArgv(params string[] extra) => new(
        Vendor:              AcpVendorDescriptors.Gemini.Vendor,
        ResolveBinaryPath:   _ => "gemini",
        ResolveDefaultModel: _ => null,
        Argv:                [.. AcpVendorDescriptors.Gemini.Argv, .. extra],
        UnattendedTrustArgv: AcpVendorDescriptors.Gemini.UnattendedTrustArgv,
        SupportsUnattended:  true,
        ModelSelector:       AcpVendorDescriptors.Gemini.ModelSelector,
        SupportsMcpServers:  true,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail);

    /// <summary>
    /// A contributor added to the Gemini argv must fail the launch, not sail through. This is the mutant the
    /// assertion exists for, and the reason the assertion is kept even though it cannot fire today.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnExtraArgvToken_FailsTheLaunch(bool isReviewFlow) {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                GeminiWithExtraArgv("--yolo"), EnabledConfig, Ctx(isReviewFlow),
                resolveGeminiVersion: _ => CertifiedVersion));

        await Assert.That(ex!.Message).Contains("gemini_launch_argv_not_canonical");
    }

    /// <summary>Even an innocuous-looking addition fails: the guard is a whole-vector template, so it does
    /// not need to recognise the token as dangerous — which is why it needs no model of the option grammar.</summary>
    [Test]
    public async Task AnInnocuousExtraToken_AlsoFailsTheLaunch() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                GeminiWithExtraArgv("--telemetry-off"), EnabledConfig, Ctx(isReviewFlow: true),
                resolveGeminiVersion: _ => CertifiedVersion));

        await Assert.That(ex!.Message).Contains("gemini_launch_argv_not_canonical");
    }

    /// <summary>
    /// The canonical vectors must NOT throw — a guard that rejects everything is not a guard.
    ///
    /// <para>The vectors are written out INDEPENDENTLY rather than fetched from
    /// <c>ExpectedGeminiArgv</c>. Review caught the earlier version feeding that helper straight back into an
    /// assertion that derives its expectation from the same helper — tautological, and it would have passed
    /// with the helper returning anything at all.</para>
    /// </summary>
    [Test]
    public async Task TheCanonicalReviewVector_PassesTheAssertion() {
        string[] argv = [
            "--experimental-acp", "--skip-trust",
            "--allowed-mcp-server-names", Identity.ResultChannelWireName,
            "--approval-mode", "yolo"
        ];

        AcpHostedAgentRuntimeFactory.AssertGeminiArgvIsCanonical(
            argv, isReviewFlow: true, Identity, reviewGate: Identity.ResultChannelWireName);

        await Assert.That(argv).Count().IsEqualTo(6);
    }

    [Test]
    public async Task TheCanonicalInteractiveVector_PassesTheAssertion() {
        string[] argv = [
            "--experimental-acp", "--skip-trust",
            "--allowed-mcp-server-names", Identity.UnmatchableMcpName
        ];

        AcpHostedAgentRuntimeFactory.AssertGeminiArgvIsCanonical(argv, isReviewFlow: false, Identity, reviewGate: null);

        await Assert.That(argv).Count().IsEqualTo(4);
    }

    /// <summary>A wrong approval VALUE is rejected, not just a missing option — `default` would silently
    /// restore prompting while satisfying any count-based check.</summary>
    [Test]
    public async Task AWrongApprovalModeValue_FailsTheAssertion() {
        string[] argv = [
            "--experimental-acp", "--skip-trust",
            "--allowed-mcp-server-names", Identity.ResultChannelWireName,
            "--approval-mode", "default"
        ];

        var ex = Assert.Throws<InvalidOperationException>(
            () => AcpHostedAgentRuntimeFactory.AssertGeminiArgvIsCanonical(
                argv, true, Identity, Identity.ResultChannelWireName));

        await Assert.That(ex!.Message).Contains("not_canonical");
    }

    /// <summary>And a swapped allowlist value — the impersonation shape — is rejected.</summary>
    [Test]
    public async Task AnAllowlistNamingSomethingElse_FailsTheAssertion() {
        string[] argv = [
            "--experimental-acp", "--skip-trust",
            "--allowed-mcp-server-names", "kcap-flow-result",
            "--approval-mode", "yolo"
        ];

        var ex = Assert.Throws<InvalidOperationException>(
            () => AcpHostedAgentRuntimeFactory.AssertGeminiArgvIsCanonical(
                argv, true, Identity, Identity.ResultChannelWireName));

        await Assert.That(ex!.Message).Contains("not_canonical");
    }

    /// <summary>A review launch whose gate the caller failed to compute cannot be asserted canonical —
    /// asserting against a re-derived gate would let the gate and the assertion drift apart.</summary>
    [Test]
    public async Task AReviewVectorWithoutAGateValue_FailsTheAssertion() {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AcpHostedAgentRuntimeFactory.AssertGeminiArgvIsCanonical(
                ["--experimental-acp"], isReviewFlow: true, Identity, reviewGate: null));

        await Assert.That(ex!.Message).Contains("gemini_review_gate_missing");
    }
}
