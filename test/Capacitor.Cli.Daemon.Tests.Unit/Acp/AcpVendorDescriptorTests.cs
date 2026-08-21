using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Test plan item 7 (Round 2 Finding 2 drops the throwing-construction cases from an
/// earlier revision of this design): pins <see cref="AcpVendorDescriptors.Cursor"/>'s literal
/// field values against today's hard-coded constants — a lightweight guard against an accidental
/// edit to the shared descriptor silently changing Cursor's behavior. There is no
/// <c>SupportsModelSelection</c> flag/invariant left to test — <see cref="AcpVendorDescriptor"/>
/// accepts any <see cref="IAcpModelSelector"/> for <see cref="AcpVendorDescriptor.ModelSelector"/>
/// unconditionally, so the one thing worth asserting is that Cursor's real descriptor constructs
/// successfully and round-trips through equality as expected.
/// </summary>
public class AcpVendorDescriptorTests {
    [Test]
    public async Task Cursor_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Cursor;

        await Assert.That(descriptor.Vendor).IsEqualTo("cursor");
        await Assert.That(descriptor.Argv.SequenceEqual(["acp"])).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual([
            "--force", "--approve-mcps", "--trust"
        ])).IsTrue();
        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        await Assert.That(descriptor.UnattendedInteractionPolicy).IsEqualTo(AcpUnattendedInteractionPolicy.Fail);
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsTrue();
        await Assert.That(descriptor.BorrowedReviewContainment)
            .IsEqualTo(AcpBorrowedReviewContainment.IndependentSnapshot);
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
    }

    [Test]
    public async Task Copilot_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Copilot;

        await Assert.That(descriptor.Vendor).IsEqualTo("copilot");
        await Assert.That(descriptor.Argv.SequenceEqual(["--acp", "--stdio"])).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual([
            "--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"
        ])).IsTrue();
        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        await Assert.That(descriptor.UnattendedInteractionPolicy).IsEqualTo(AcpUnattendedInteractionPolicy.AutoApprove);
        // ACP still advertises only HTTP/SSE, so session/new stdio forwarding stays disabled.
        await Assert.That(descriptor.SupportsMcpServers).IsFalse();
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.CopilotAdditionalConfig);
        // Copilot's borrowed-review capability is NOT declared statically: it is resolved per
        // platform by CopilotBorrowedReviewPolicy, because the tool surface that makes a borrowed
        // snapshot readable AND contained has only been verified on some platforms. Pinning a static
        // declaration here would pin an answer that no launch consults.
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
        await Assert.That(descriptor.BorrowedReviewContainment)
            .IsEqualTo(AcpBorrowedReviewContainment.None);
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
    }

    [Test]
    public async Task Copilot_ResolveBinaryPath_ReadsConfigCopilotPath() {
        var config = new DaemonConfig { CopilotPath = "/opt/copilot/copilot" };

        await Assert.That(AcpVendorDescriptors.Copilot.ResolveBinaryPath(config)).IsEqualTo("/opt/copilot/copilot");
    }

    [Test]
    public async Task Kiro_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Kiro;

        await Assert.That(descriptor.Vendor).IsEqualTo("kiro");
        await Assert.That(descriptor.Argv.SequenceEqual(["acp"])).IsTrue();

        // Unattended review is ON. The containment that was missing is now source suppression: a
        // review launch runs with a daemon-owned EMPTY KIRO_HOME (so the operator's global
        // ~/.kiro/settings/mcp.json servers, kcap-flows among them, do not initialize), and
        // branch-authored workspace config is removed at the worktree layer.
        await Assert.That(descriptor.SupportsUnattended).IsTrue();

        // The fixed trust argv stays EMPTY and the BUILDER carries it, because the value depends on
        // what this launch injects: a review with an MCP allowlist gets servers whose tools a fixed
        // list could not name, and under Fail their first call would end the round. The constructor
        // rejects carrying both.
        await Assert.That(descriptor.UnattendedTrustArgv.IsEmpty).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgvBuilder).IsNotNull();

        // AllowlistedAutoApprove, measured rather than preferred. Fail's premise -- a scoped-trust
        // reviewer raises no frame -- is false on kiro-cli 2.16.0: a live round raised one for the
        // result tool that IS in this launch's trust list. AutoApprove is not the alternative, since
        // it does not inspect the tool at all.
        await Assert.That(descriptor.UnattendedInteractionPolicy)
            .IsEqualTo(AcpUnattendedInteractionPolicy.AllowlistedAutoApprove);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
        await Assert.That(descriptor.BorrowedReviewContainment)
            .IsEqualTo(AcpBorrowedReviewContainment.None);

        // TRUE here while Copilot is FALSE, on the same advertised ACP mcpCapabilities shape
        // ({http, sse} — no stdio). Not a contradiction: Copilot's false is an empirical finding about
        // Copilot, and Kiro was probed to a real tools/call with a stdio server passed in
        // session/new.mcpServers — the nonce reached the model. Kiro honours stdio without advertising
        // it. server_initialized alone would NOT have justified this: it proves a server started, not
        // that its tools are callable.
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ReviewFlowMcpTransport)
            .IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);

        // SetModelSelector, not ConfigOptionModelSelector: probe-measured (docs/probes/
        // 2026-08-05-kiro-model-override/, kiro-cli 2.16.0) — session/set_config_option does not
        // exist on Kiro (-32601 Method not found), while session/set_model succeeds AND takes
        // effect: the very next turn's backend request carried the requested modelId, the reply
        // self-identified as it, and Kiro's own session state persisted it with model-specific
        // parameters. Not NoOp either — that deferral existed only while the write half was
        // unverified.
        await Assert.That(descriptor.ModelSelector).IsEqualTo(SetModelSelector.Instance);
    }

    [Test]
    public async Task Kiro_ResolveBinaryPath_ReadsConfigKiroPath() {
        var config = new DaemonConfig { KiroPath = "/opt/kiro/kiro-cli" };

        await Assert.That(AcpVendorDescriptors.Kiro.ResolveBinaryPath(config)).IsEqualTo("/opt/kiro/kiro-cli");
    }

    /// <summary>
    /// The zero-configuration case, and the one an env-precedence test cannot cover: with nothing set,
    /// the descriptor must resolve the name a standard install actually puts on PATH.
    ///
    /// <para><c>KiroPath</c> predates this descriptor and defaulted to <c>"kiro"</c> while nothing
    /// consumed it. <c>kiro</c> is not the shipped binary — <c>kiro-cli</c> is, and it is what
    /// <c>PluginCommand.KiroBinary</c> resolves. Because availability is
    /// <c>CliResolver.Exists(KiroPath)</c>, the old default meant Kiro was silently never advertised
    /// on a correct install until an operator discovered <c>KCAP_KIRO_PATH</c>. An override test
    /// passes identically whichever name the default holds, which is precisely how the wrong default
    /// survived; this test is the one that fails.</para>
    /// </summary>
    [Test]
    public async Task Kiro_ZeroConfiguration_ResolvesTheShippedBinaryName() {
        await Assert.That(AcpVendorDescriptors.Kiro.ResolveBinaryPath(new DaemonConfig()))
            .IsEqualTo("kiro-cli");
    }

    /// <summary>Zero-configuration behaviour is unchanged from the no-override era: with no
    /// <c>KiroModel</c> configured the descriptor offers no daemon-wide default — Kiro runs its own
    /// default model and none is reported — and ANOTHER vendor's model field must never leak in.
    /// When <c>KiroModel</c> IS configured it is the daemon-wide default, resolved against
    /// <c>session/new</c>'s <c>availableModels</c> at launch like Cursor's.</summary>
    [Test]
    public async Task Kiro_ResolveDefaultModel_ReadsConfigKiroModel_NullByDefault() {
        await Assert.That(AcpVendorDescriptors.Kiro.ResolveDefaultModel(new DaemonConfig())).IsNull();
        await Assert.That(AcpVendorDescriptors.Kiro.ResolveDefaultModel(
            new DaemonConfig { CursorModel = "claude-opus-4-8" })).IsNull();
        await Assert.That(AcpVendorDescriptors.Kiro.ResolveDefaultModel(
            new DaemonConfig { KiroModel = "claude-haiku-4.5" })).IsEqualTo("claude-haiku-4.5");
    }

    [Test]
    public async Task Cursor_ResolveBinaryPath_ReadsConfigCursorPath() {
        var config = new DaemonConfig { CursorPath = "/opt/cursor/cursor-agent" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveBinaryPath(config)).IsEqualTo("/opt/cursor/cursor-agent");
    }

    [Test]
    public async Task Cursor_ResolveDefaultModel_ReadsConfigCursorModel() {
        var config = new DaemonConfig { CursorModel = "claude-opus-4-8" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveDefaultModel(config)).IsEqualTo("claude-opus-4-8");
    }

    /// <summary>Any <see cref="IAcpModelSelector"/> — including a NoOp one, even though the real
    /// Cursor descriptor never uses it — constructs a valid descriptor. There is no invariant left
    /// to reject this combination (Round 2 Finding 2).</summary>
    [Test]
    public async Task Descriptor_ConstructsSuccessfully_WithAnyModelSelector() {
        var descriptor = new AcpVendorDescriptor(
            Vendor:              "test-vendor",
            ResolveBinaryPath:   _ => "test-vendor-cli",
            ResolveDefaultModel: _ => null,
            Argv:                ["acp"],
            UnattendedTrustArgv: [],
            SupportsUnattended:  false,
            ModelSelector:       NoOpModelSelector.Instance,
            SupportsMcpServers:  false
        );

        await Assert.That(descriptor.ModelSelector).IsEqualTo(NoOpModelSelector.Instance);
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.Unsupported);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
    }

    /// <summary>Qodo finding 3: a vendor that doesn't support unattended launches must not carry
    /// any <see cref="AcpVendorDescriptor.UnattendedTrustArgv"/> — the constructor enforces this
    /// invariant rather than relying solely on the orchestrator's external gate.</summary>
    [Test]
    public async Task Constructor_Throws_WhenUnattendedTrustArgvNonEmpty_AndSupportsUnattendedFalse() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:              "test-vendor",
            ResolveBinaryPath:   _ => "test-vendor-cli",
            ResolveDefaultModel: _ => null,
            Argv:                ["acp"],
            UnattendedTrustArgv: ["--trust"],
            SupportsUnattended:  false,
            ModelSelector:       NoOpModelSelector.Instance,
            SupportsMcpServers:  false
        )).Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_Throws_WhenSessionNewTransportLacksMcpServerSupport() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:                 "test-vendor",
            ResolveBinaryPath:      _ => "test-vendor-cli",
            ResolveDefaultModel:    _ => null,
            Argv:                   ["acp"],
            UnattendedTrustArgv:    ["--trust"],
            SupportsUnattended:     true,
            ModelSelector:          NoOpModelSelector.Instance,
            SupportsMcpServers:     false,
            ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
            UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove
        )).Throws<ArgumentException>();
    }

    /// <summary>The zero-configuration case, for the same reason Kiro needs one: an override test passes
    /// identically whichever name the default holds, which is how Kiro's wrong default survived. Gemini's
    /// default is already correct — the binary really is <c>gemini</c> — and this is the test that would
    /// notice if it stopped being.</summary>
    [Test]
    public async Task Gemini_ZeroConfiguration_ResolvesTheShippedBinaryName() {
        await Assert.That(AcpVendorDescriptors.Gemini.ResolveBinaryPath(new DaemonConfig()))
            .IsEqualTo("gemini");
    }

    [Test]
    public async Task Gemini_ResolveBinaryPath_ReadsConfigGeminiPath() {
        var config = new DaemonConfig { GeminiPath = "/opt/gemini/gemini" };

        await Assert.That(AcpVendorDescriptors.Gemini.ResolveBinaryPath(config)).IsEqualTo("/opt/gemini/gemini");
    }

    [Test]
    public async Task Gemini_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Gemini;

        await Assert.That(descriptor.Vendor).IsEqualTo("gemini");

        // --skip-trust is REQUIRED, not optional: Gemini refuses a headless turn in an untrusted
        // directory outright (exit 55, before any model call) and a daemon worktree cannot be assumed
        // pre-trusted. It is NOT containment — see the allowlist assertion below.
        await Assert.That(descriptor.Argv.SequenceEqual(
            ["--experimental-acp", "--skip-trust",
             "--allowed-mcp-server-names", AcpVendorDescriptors.UnmatchableMcpNamePlaceholder])).IsTrue();

        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        // --approval-mode yolo, and ONLY on a review launch. Measured: without it Gemini gates its
        // own injected result-channel tool behind session/request_permission, which no human answers, so the
        // reviewer cannot report. It must never appear in Argv — an interactive hosted session has to behave
        // as the user's own does.
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual(["--approval-mode", "yolo"])).IsTrue();
        await Assert.That(descriptor.Argv.Contains("--approval-mode")).IsFalse();
        await Assert.That(descriptor.Argv.Contains("--yolo")).IsFalse();
        await Assert.That(descriptor.UnattendedTrustArgv.Contains("--yolo")).IsFalse();
        await Assert.That(descriptor.UnattendedInteractionPolicy)
            .IsEqualTo(AcpUnattendedInteractionPolicy.Fail);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();

        // FALSE pending a call-level stdio probe. Gemini advertises {http, sse} and not stdio — but that
        // advertisement is not a discriminator (Kiro honours stdio without advertising it), so flipping
        // this needs a purpose-built stdio server driven to a real tools/call, not an inference.
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();

        // Same call as Kiro: session/new returns models so the read half fits, but the write half is
        // unverified and ConfigOptionModelSelector fails SILENTLY.
        await Assert.That(descriptor.ModelSelector).IsEqualTo(NoOpModelSelector.Instance);
        await Assert.That(descriptor.ModelSelector.CanSelectModel).IsFalse();
    }

    /// <summary>
    /// The allowlist contents and <c>SupportsMcpServers</c> are COUPLED, and this is the test that stops
    /// them drifting.
    ///
    /// <para>An allowlist of one non-matching name permits nothing. That is correct only while nothing is
    /// injected. The day the stdio probe flips <c>SupportsMcpServers</c> to true, the allowlist must
    /// become the injected server names in the same change — otherwise hosted Gemini ships with MCP
    /// silently broken, which is exactly the failure a green descriptor test would otherwise hide.</para>
    ///
    /// <para>The sentinel must also be non-empty: <c>--allowed-mcp-server-names ""</c> fails Gemini's
    /// config load before the session starts.</para>
    /// </summary>
    [Test]
    public async Task Gemini_McpAllowlist_IsCoupledToSupportsMcpServers() {
        var argv = AcpVendorDescriptors.Gemini.Argv.ToArray();

        // The option must be present EXACTLY once, and have a following value.
        //
        // An earlier version read `argv[IndexOf(flag) + 1]` and asserted only "not the old sentinel". Two
        // ways that was vacuous, both caught in review: with the option ABSENT, IndexOf returns -1 and the
        // expression reads argv[0] — a non-empty string that differs from the sentinel, so the alleged
        // coupling passed with no allowlist at all; and a SECOND occurrence, whose value may win at
        // argument parsing, was invisible to it.
        var occurrences = argv.Count(a => a == "--allowed-mcp-server-names");
        await Assert.That(occurrences).IsEqualTo(1);

        var flagAt = Array.IndexOf(argv, "--allowed-mcp-server-names");
        await Assert.That(flagAt).IsGreaterThanOrEqualTo(0);
        await Assert.That(flagAt + 1).IsLessThan(argv.Length);

        var allowed = argv[flagAt + 1];
        await Assert.That(allowed).IsNotEmpty();

        // the hosting work wrote the SupportsMcpServers==true branch as a guess: that flipping the flag would make the
        // DESCRIPTOR carry the injected server names. Measured, the coupling lives one layer down — the
        // template always holds the placeholder, and the review LAUNCH replaces it with that launch's
        // result-channel wire name, because the name is per-launch and so cannot be a constant.
        //
        // The coupling it was reaching for is asserted where it actually happens: see
        // AcpHostedAgentRuntimeFactoryTests' canonical-argv tests, which compare the whole emitted vector
        // for both launch kinds.
        await Assert.That(allowed).IsEqualTo(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder);
    }

    /// <summary>
    /// The deny-all name must not be a literal the reviewed repository can match.
    ///
    /// <para>This is the finding that broke the first version: it passed <c>kcap-none</c> and asserted in a
    /// comment that no server would ever be called that. A contributor controls
    /// <c>.gemini/settings.json</c> and can name their server <c>kcap-none</c> — measured, it executes, and
    /// the clamp is bypassed entirely. So the descriptor carries a PLACEHOLDER and the factory substitutes
    /// an unguessable value per launch; this test pins that the descriptor never carries a usable literal
    /// again.</para>
    /// </summary>
    [Test]
    public async Task Gemini_DenyAllMcpName_IsAPlaceholderNotAMatchableLiteral() {
        var argv    = AcpVendorDescriptors.Gemini.Argv.ToArray();
        var allowed = argv[Array.IndexOf(argv, "--allowed-mcp-server-names") + 1];

        await Assert.That(allowed).IsEqualTo(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder);

        // A placeholder is substituted before launch, so it must be recognisable as one rather than
        // looking like a plausible server name someone might ship as-is.
        await Assert.That(allowed).StartsWith("__");
        await Assert.That(allowed).EndsWith("__");
    }

    /// <summary>
    /// Gemini's launch-failure hint must read as a POSSIBILITY. Gemini reports a missing project with a
    /// message naming a tier problem — thrown by <c>throwIneligibleOrProjectIdError</c>, the same text for
    /// both causes — and reproducing that confidently-wrong attribution is the failure mode this guards.
    ///
    /// <para>Golden-ish rather than a word blacklist: the specific hedges and the daemon-not-your-shell
    /// clause are each asserted, so rewording that removes the hedging fails rather than passing a
    /// keyword scan.</para>
    ///
    /// <para><b>It is a GOLDEN test:</b> the approved wording, held independently here, must match
    /// exactly.</para>
    ///
    /// <para>The first version asserted a few required phrases and two forbidden ones, which review
    /// correctly called a fig leaf — a hint could keep both hedges and then append "the failure is caused
    /// by an ineligible account" and still pass. Substring checks cannot establish "does not diagnose"
    /// about arbitrary prose. Equality can: any rewording is a deliberate edit to a pinned expectation,
    /// and whoever makes it has to justify the new text rather than slip past a keyword scan.</para>
    /// </summary>
    [Test]
    public async Task GeminiAuthHint_MatchesTheApprovedWordingExactly() {
        const string approved =
            "this may be an authentication or project-configuration problem, or it may be unrelated — if "
          + "hosted Gemini has not worked on this machine before, check `gemini` is logged in and that "
          + "GOOGLE_CLOUD_PROJECT (or GOOGLE_CLOUD_PROJECT_ID) is set where the DAEMON can see it (the "
          + "service unit, not your shell profile), then re-run `kcap daemon service install` and restart "
          + "the daemon";

        await Assert.That(AcpHostedAgentRuntime.GeminiAuthHint).IsEqualTo(approved);
    }

    [Test]
    public async Task Constructor_Throws_WhenBorrowedReviewSupportLacksUnattendedSupport() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:                      "test-vendor",
            ResolveBinaryPath:           _ => "test-vendor-cli",
            ResolveDefaultModel:         _ => null,
            Argv:                        [],
            UnattendedTrustArgv:         [],
            SupportsUnattended:          false,
            ModelSelector:               NoOpModelSelector.Instance,
            SupportsMcpServers:          false,
            SupportsBorrowedReviewFlow: true
        )).Throws<ArgumentException>();
    }

    /// <summary>Reconnect eligibility is a PROBE-VERIFIED per-vendor fact (the 2026-08-04 C0
    /// re-probe, docs/probes/2026-08-04-acp-reconnect-c0/), never inferred from the advertised
    /// loadSession capability — all four vendors advertise it, two measurably cannot honor it
    /// across a crashed owner. Flipping Kiro or Gemini requires a passing probe re-run.</summary>
    [Test]
    public async Task Reconnect_resume_is_probe_verified_per_vendor() {
        await Assert.That(AcpVendorDescriptors.Cursor.SupportsReconnectResume).IsTrue();
        await Assert.That(AcpVendorDescriptors.Copilot.SupportsReconnectResume).IsTrue();
        await Assert.That(AcpVendorDescriptors.Kiro.SupportsReconnectResume).IsFalse();
        await Assert.That(AcpVendorDescriptors.Gemini.SupportsReconnectResume).IsFalse();
    }

    /// <summary>
    /// Kiro aliases (its MCP tripwire compares launch-unique names) but carries NO exact-name MCP
    /// allowlist argv. One predicate used to gate both, so turning aliasing on for Kiro without this
    /// split would run Gemini's placeholder substitution and canonical-argv assertion against a vendor
    /// that has neither, and route it through Gemini's capability gate.
    /// </summary>
    [Test]
    public async Task Kiro_AliasesItsResultChannel_ButCarriesNoMcpNameAllowlistArgv() {
        var kiro = AcpVendorDescriptors.Kiro;

        await Assert.That(AcpHostedAgentRuntimeFactory.AliasesResultChannel(kiro)).IsTrue();
        await Assert.That(AcpHostedAgentRuntimeFactory.UsesMcpNameAllowlistArgv(kiro)).IsFalse();
        await Assert.That(kiro.Argv.Contains(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder)).IsFalse();
    }

    /// <summary>The control: Gemini must keep BOTH, or the split silently disabled its clamp.</summary>
    [Test]
    public async Task Gemini_KeepsBothAliasingAndTheAllowlistArgv() {
        var gemini = AcpVendorDescriptors.Gemini;

        await Assert.That(AcpHostedAgentRuntimeFactory.AliasesResultChannel(gemini)).IsTrue();
        await Assert.That(AcpHostedAgentRuntimeFactory.UsesMcpNameAllowlistArgv(gemini)).IsTrue();
        await Assert.That(gemini.Argv.Contains(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder)).IsTrue();
    }
}
