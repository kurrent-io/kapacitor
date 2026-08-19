using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.OpenCode;

/// <summary>
/// The OpenCode reviewer's launch shape, asserted on the ENVIRONMENT the process would receive —
/// which for this vendor is the whole trust vector, since <c>opencode acp</c> has no trust argv. An
/// argv assertion here would certify nothing.
/// </summary>
public class OpenCodeReviewerLaunchTests {
    const string InstalledVersion = "1.18.9";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    DaemonConfig EnabledConfig(DaemonStore? paths = null) {
        var config = new DaemonConfig {
            OpenCodeUnattendedReviewerEnabled = true,
            Store = paths ?? Daemons.Store,
            Name = "test-daemon",
            DaemonEpoch = "epoch-1"
        };

        // Seeded exactly as enabling the reviewer does in production: without it every launch is
        // refused over an upgrade that never happened.
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.OpenCode.Vendor)
            .Affirm(InstalledVersion);

        return config;
    }

    static RuntimeStartContext Ctx(bool isReviewFlow, string[]? mcpAllowlist = null) => new RuntimeStartContext(
        AgentId: "agent-1", Vendor: "opencode", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: isReviewFlow ? "http://kcap.test" : null,
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap")
        with { McpAllowlist = mcpAllowlist };

    System.Diagnostics.ProcessStartInfo Psi(
            bool isReviewFlow, DaemonConfig? config = null, string[]? mcpAllowlist = null) =>
        AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.OpenCode, config ?? EnabledConfig(), Ctx(isReviewFlow, mcpAllowlist),
            resolveGeminiVersion: _ => InstalledVersion);

    static JsonElement Permission(System.Diagnostics.ProcessStartInfo psi) =>
        JsonDocument.Parse(psi.Environment[OpenCodeLaunchEnvironment.PermissionVariable]!).RootElement;

    static void SkipOnWindows() =>
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The OpenCode unattended reviewer is POSIX-only: its containment is an EMPTY config "
          + "directory, which cannot be created owner-only on Windows.");

    [Test]
    public async Task Descriptor_IsUnattendedCapableAndFailsOnAnyInteractionFrame() {
        var descriptor = AcpVendorDescriptors.OpenCode;

        await Assert.That(descriptor.SupportsUnattended).IsTrue();

        // Fail, not AutoApprove: on the shipped permission table a denied tool is ABSENT from the
        // model's surface rather than refused when called, so a correctly-configured reviewer raises no
        // frame at all. A frame therefore means the launch contract regressed.
        await Assert.That(descriptor.UnattendedInteractionPolicy)
            .IsEqualTo(AcpUnattendedInteractionPolicy.Fail);

        // The result channel rides session/new — measured at call level, unlike Copilot.
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);

        // No containment boundary has been established for a reviewer in the caller's live checkout.
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
    }

    /// <summary>
    /// The read family the reviewer needs, and nothing that could change the worktree or reach the
    /// network. Asserted against the BUILT document, with the expected allow-set written out as a
    /// LITERAL.
    ///
    /// <para><b>Why a literal and not <c>OpenCodeReviewerPermissions.ReadTools</c>.</b> Iterating the
    /// source list makes it its own oracle: adding <c>bash</c> to <c>ReadTools</c> and dropping it from
    /// <c>ForbiddenTools</c> would widen the reviewer to a shell and keep this test green, because both
    /// halves of the comparison moved together. The same trap
    /// <c>AcpHostedAgentRuntimeFactory.ExpectedGeminiArgv</c> documents for argv. Written out, a widened
    /// posture goes red and a deliberate widening has to be restated here.</para>
    /// </summary>
    [Test]
    public async Task AReviewLaunch_AllowsOnlyReadToolsAndDeniesEverythingElse() {
        SkipOnWindows();

        var permission = Permission(Psi(isReviewFlow: true));

        await Assert.That(permission.GetProperty("*").GetString()).IsEqualTo("deny");

        // Every allowed NATIVE tool (the `{server}_*` MCP entries are asserted separately, since their
        // names are per-launch aliases).
        var allowedNative = permission.EnumerateObject()
            .Where(p => p.Value.GetString() == "allow"
                     && !p.Name.EndsWith("_*", StringComparison.Ordinal))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(allowedNative).IsEquivalentTo(new[] { "glob", "grep", "list", "read" });

        // Named individually so the failure message says WHICH capability leaked, and so this stays a
        // real assertion rather than a restatement of the source list.
        foreach (var forbidden in new[] { "write", "edit", "patch", "bash", "webfetch", "websearch",
                                          "task", "skill", "todowrite" }) {
            if (permission.TryGetProperty(forbidden, out var rule))
                await Assert.That(rule.GetString()).IsNotEqualTo("allow");

            await Assert.That(allowedNative).DoesNotContain(forbidden);
        }
    }

    /// <summary>
    /// The result channel must be admitted by name, and this is load-bearing rather than
    /// belt-and-braces: measured, with the entry removed the injected tool is absent from the model's
    /// toolset entirely, so the reviewer could not report at all.
    /// </summary>
    [Test]
    public async Task AReviewLaunch_AdmitsTheInjectedResultChannelByItsFlattenedName() {
        SkipOnWindows();

        var psi = Psi(isReviewFlow: true);
        var permission = Permission(psi);

        var admitted = permission.EnumerateObject()
            .Where(p => p.Name.EndsWith("_*", StringComparison.Ordinal) && p.Value.GetString() == "allow")
            .Select(p => p.Name)
            .ToArray();

        // At least the flow-result channel. The name is per-launch (aliased), so this asserts the
        // SHAPE and non-emptiness rather than a literal a launch does not have to use.
        await Assert.That(admitted).IsNotEmpty();
    }

    /// <summary>
    /// The admitted names must be the names <c>session/new</c> actually injects. Two derivations of the
    /// same names fail SILENTLY — the reviewer starts normally and can never call its own channel — so
    /// this compares the permission document against the built MCP list rather than trusting both.
    /// </summary>
    [Test]
    public async Task TheAdmittedNames_AreExactlyTheInjectedServerNames() {
        SkipOnWindows();

        var config = EnabledConfig();
        var ctx    = Ctx(isReviewFlow: true);

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.OpenCode, config, ctx, resolveGeminiVersion: _ => InstalledVersion);

        var admitted = Permission(psi).EnumerateObject()
            .Where(p => p.Name.EndsWith("_*", StringComparison.Ordinal))
            .Select(p => p.Name[..^2])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Every admitted name must be a real MCP server wire name, and none may be a native tool
        // masquerading as one.
        await Assert.That(admitted).IsNotEmpty();
        foreach (var name in admitted)
            await Assert.That(OpenCodeReviewerPermissions.ReadTools).DoesNotContain(name);
    }

    /// <summary>The recursion guard: without an isolated config dir the reviewer inherits the
    /// operator's global MCP servers, the flow-starting one included.</summary>
    [Test]
    public async Task AReviewLaunch_GetsAnEmptyIsolatedConfigDirectory() {
        SkipOnWindows();

        var psi = Psi(isReviewFlow: true, EnabledConfig(Daemons.Store));

        var dir = psi.Environment[OpenCodeLaunchEnvironment.ConfigDirVariable];

        await Assert.That(dir).IsNotNull();
        await Assert.That(Directory.Exists(dir!)).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(dir!)).IsEmpty();
        // Under THIS daemon's own root, never a shared one — a shared root's "delete every directory
        // whose epoch is not mine" rule would select a peer daemon's live directory.
        await Assert.That(dir!.StartsWith(
            OpenCodeReviewerConfigDir.RootFor(
                AcpHostedAgentRuntimeFactory.ReviewerStateDir(
                    new DaemonConfig { Store = Daemons.Store, Name = "test-daemon" })),
            StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The reviewed BRANCH is not trusted input: its own opencode config and its
    /// AGENTS.md/CLAUDE.md must not reach the reviewer judging it.</summary>
    [Test]
    public async Task AReviewLaunch_SuppressesBranchAuthoredConfiguration() {
        SkipOnWindows();

        var psi = Psi(isReviewFlow: true);

        await Assert.That(psi.Environment[OpenCodeLaunchEnvironment.ProjectConfigVariable]).IsEqualTo("1");
    }

    /// <summary>
    /// None of the reviewer containment may leak onto an interactive session, which must behave as the
    /// user's own does. The plugin suppression is the ONE setting both share.
    /// </summary>
    [Test]
    public async Task AnInteractiveLaunch_GetsNoneOfTheReviewerContainment() {
        var psi = Psi(isReviewFlow: false);

        await Assert.That(psi.Environment[OpenCodeLaunchEnvironment.PureVariable]).IsEqualTo("1");
        await Assert.That(psi.Environment.ContainsKey(OpenCodeLaunchEnvironment.PermissionVariable)).IsFalse();
        await Assert.That(psi.Environment.ContainsKey(OpenCodeLaunchEnvironment.ConfigDirVariable)).IsFalse();
        await Assert.That(psi.Environment.ContainsKey(OpenCodeLaunchEnvironment.ProjectConfigVariable)).IsFalse();
    }

    /// <summary>
    /// The gate is the launch boundary, not just advertisement: an explicit <c>vendor: "opencode"</c>
    /// request can reach a launch without consulting the advertised list.
    /// </summary>
    [Test]
    public async Task AReviewLaunch_IsRefusedWhenTheOperatorHasNotConsented() {
        SkipOnWindows();

        var config = new DaemonConfig {
            OpenCodeUnattendedReviewerEnabled = false,
            Store = Daemons.Store, Name = "test-daemon", DaemonEpoch = "epoch-1"
        };

        await Assert.That(() => Psi(isReviewFlow: true, config))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_unattended_reviewer_disabled");
    }

    /// <summary>
    /// Consent alone is not enough: with no recorded minimum there is nothing to check the installed
    /// build against, and the containment this reviewer relies on is a behaviour of the build.
    /// </summary>
    [Test]
    public async Task AReviewLaunch_IsRefusedWithNoRecordedMinimum() {
        SkipOnWindows();

        var config = new DaemonConfig {
            OpenCodeUnattendedReviewerEnabled = true,
            Store = Daemons.Store, Name = "test-daemon", DaemonEpoch = "epoch-1"
        };

        await Assert.That(() => Psi(isReviewFlow: true, config))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_reviewer_version_no_minimum");
    }

    /// <summary>The launch budget and the cleanup hook must agree: a budget with no cleanup strands
    /// exactly the directory the budget fired to reclaim.</summary>
    [Test]
    public async Task AReviewLaunch_HasABoundedLaunchBudget() {
        var config = EnabledConfig();

        await Assert.That(AcpHostedAgentRuntimeFactory.ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptors.OpenCode, config, Ctx(isReviewFlow: true)))
            .IsEqualTo(config.OpenCodeReviewerLaunchTimeoutSeconds);

        await Assert.That(AcpHostedAgentRuntimeFactory.ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptors.OpenCode, config, Ctx(isReviewFlow: false))).IsNull();
    }

    /// <summary>
    /// The generalization must not have changed Kiro's budget — it was the only vendor with one before
    /// OpenCode joined, and the three sites that consult it were separate conditionals.
    /// </summary>
    [Test]
    public async Task TheSharedBudget_StillReportsKirosAndNoOtherVendors() {
        var config = new DaemonConfig();
        var kiroCtx = Ctx(isReviewFlow: true) with { Vendor = "kiro" };

        await Assert.That(AcpHostedAgentRuntimeFactory.ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptors.Kiro, config, kiroCtx))
            .IsEqualTo(config.KiroReviewerLaunchTimeoutSeconds);

        await Assert.That(AcpHostedAgentRuntimeFactory.ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptors.Gemini, config, Ctx(isReviewFlow: true) with { Vendor = "gemini" })).IsNull();

        await Assert.That(AcpHostedAgentRuntimeFactory.ReviewerLaunchTimeoutSeconds(
            AcpVendorDescriptors.Cursor, config, Ctx(isReviewFlow: true) with { Vendor = "cursor" })).IsNull();
    }

    /// <summary>
    /// An unnamed injected server would silently produce a permission key of <c>_*</c>, admitting
    /// nothing the reviewer needs while looking populated. Refuse instead.
    /// </summary>
    [Test]
    public async Task AnUnnamedInjectedServer_IsRefusedRatherThanAdmittedAsAnEmptyPrefix() {
        var unnamed = new AcpMcpServerSpec(Name: "", Command: "kcap", Args: [], Env: []);

        await Assert.That(() => OpenCodeReviewerPermissions.Build([unnamed]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_reviewer_permission_unnamed_server");
    }

    /// <summary>
    /// The name is interpolated into a GLOB, so anything a glob engine reads as syntax would widen the
    /// entry past that server. Unreachable today (every injected name is a per-launch alias this daemon
    /// generated) — the guard exists so the day a name starts coming from elsewhere is a refusal rather
    /// than a wider surface.
    ///
    /// <para>The arguments deliberately range past the obvious <c>* ? [ ]</c> into extglob, brace, pipe
    /// and backslash syntax: an earlier revision enumerated metacharacters and review pointed out the
    /// list could not be exhaustive for an engine we do not control. The implementation is now a
    /// character ALLOWLIST, which is total by construction, and these cases pin that.</para>
    /// </summary>
    [Test]
    [Arguments("kcap-*")]
    [Arguments("kcap-?")]
    [Arguments("kcap-[a-z]")]
    [Arguments("kcap!")]
    [Arguments("kcap-?(a|b)")]
    [Arguments("kcap-{a,b}")]
    [Arguments("kcap|other")]
    [Arguments("kcap\\other")]
    [Arguments("kcap other")]
    [Arguments("kcap/other")]
    public async Task AnUnsafeCharacterInAnInjectedServerName_IsRefused(string name) {
        var spec = new AcpMcpServerSpec(Name: name, Command: "kcap", Args: [], Env: []);

        await Assert.That(() => OpenCodeReviewerPermissions.Build([spec]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_reviewer_permission_unsafe_server_name");
    }

    /// <summary>The allowlist must still admit the shapes a real per-launch alias takes, or it would
    /// refuse every actual launch — a guard that blocks the legitimate case is worse than none.</summary>
    [Test]
    [Arguments("kcap-flow-result")]
    [Arguments("kcap-flow-result-a1b2c3d4")]
    [Arguments("kcap_sessions")]
    [Arguments("kcap.review.v2")]
    public async Task AnOrdinaryInjectedServerName_IsAdmitted(string name) {
        var spec = new AcpMcpServerSpec(Name: name, Command: "kcap", Args: [], Env: []);

        var permission = JsonDocument.Parse(OpenCodeReviewerPermissions.Build([spec])).RootElement;

        await Assert.That(permission.GetProperty($"{name}_*").GetString()).IsEqualTo("allow");
    }

    /// <summary>
    /// Every forbidden tool carries its OWN <c>deny</c> key, not just wildcard coverage.
    ///
    /// <para>The measured evidence is that <c>OPENCODE_PERMISSION</c> beats an operator config saying
    /// <c>"*": "ask"</c>. It does NOT establish that a wildcard from the env beats a SPECIFIC key from a
    /// file — OpenCode merges per key and resolves specific-before-wildcard, so an operator's
    /// <c>bash: "allow"</c> could survive a bare <c>"*": "deny"</c>. Naming each tool makes the merge
    /// order irrelevant. Regression target: dropping these keys back to wildcard-only coverage.</para>
    /// </summary>
    [Test]
    [Arguments("bash")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("patch")]
    [Arguments("webfetch")]
    [Arguments("websearch")]
    [Arguments("task")]
    [Arguments("skill")]
    [Arguments("todowrite")]
    public async Task EachForbiddenTool_IsDeniedByItsOwnKeyNotOnlyByTheWildcard(string tool) {
        SkipOnWindows();

        var permission = Permission(Psi(isReviewFlow: true));

        await Assert.That(permission.TryGetProperty(tool, out var rule)).IsTrue();
        await Assert.That(rule.GetString()).IsEqualTo("deny");
    }
}
