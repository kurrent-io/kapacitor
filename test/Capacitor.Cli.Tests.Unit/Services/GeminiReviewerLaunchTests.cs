using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

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
    static readonly Guid ChannelGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    static readonly Guid DenyGuid    = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    static LaunchIdentity Identity => LaunchIdentity.FromGuids(ChannelGuid, DenyGuid, aliasResultChannel: true);

    /// <summary>A daemon that has opted in, on a certified vendor build — the only combination that launches.</summary>
    static DaemonConfig EnabledConfig => new() { GeminiUnattendedReviewerEnabled = true };

    static string CertifiedVersion => GeminiReviewerCapability.CertifiedVersions.First();

    static RuntimeStartContext Ctx(bool isReviewFlow) => new RuntimeStartContext(
        AgentId: "agent-1", Vendor: "gemini", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: isReviewFlow ? "http://kcap.test" : null,
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap") with { LaunchIdentity = Identity };

    static string[] Build(bool isReviewFlow, DaemonConfig? config = null, string? version = null) =>
        [.. AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Gemini, config ?? EnabledConfig, Ctx(isReviewFlow),
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
        var argv     = Build(isReviewFlow: true);
        var injected = AcpReviewFlowMcp.Build(Ctx(isReviewFlow: true), []);

        var flagAt  = Array.IndexOf(argv, "--allowed-mcp-server-names");
        var allowed = argv[flagAt + 1];

        await Assert.That(injected.Select(s => s.Name)).Contains(allowed);
        await Assert.That(allowed).IsEqualTo(Identity.ResultChannelWireName);
    }

    /// <summary>The allowlist must hold exactly one name: the option is array-typed and comma-coerced by the
    /// vendor, so a second entry widens the gate, and an EMPTY one disables it rather than denying all.</summary>
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
    public async Task ADisabledDaemon_RefusesAReviewLaunchBeforeAnyProcessCouldStart() {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(isReviewFlow: true, config: new DaemonConfig()));

        await Assert.That(ex!.Message).Contains("gemini_unattended_reviewer_disabled");
    }

    /// <summary>An uncertified vendor build is refused even when the operator has opted in: the reviewer's
    /// only containment is that version's MCP-allowlist semantics.</summary>
    [Test]
    [Arguments("0.54.0")]
    [Arguments("0.53.1")]
    [Arguments("")]
    public async Task AnUncertifiedOrUnresolvableVersion_RefusesAReviewLaunch(string version) {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(isReviewFlow: true, version: version));

        await Assert.That(ex!.Message).Contains("gemini_unattended_reviewer_version");
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
            LaunchIdentity = LaunchIdentity.FromGuids(ChannelGuid, DenyGuid, aliasResultChannel: false)
        };

        var argv = AcpHostedAgentRuntimeFactory
            .BuildProcessStartInfo(AcpVendorDescriptors.Cursor, new DaemonConfig(), ctx)
            .ArgumentList;

        await Assert.That(argv).DoesNotContain("--allowed-mcp-server-names");
        await Assert.That(argv).DoesNotContain("--approval-mode");

        var injected = AcpReviewFlowMcp.Build(ctx, []);
        await Assert.That(injected.Select(s => s.Name)).Contains(KcapMcpRegistry.ReservedResultChannelId);
    }
}
