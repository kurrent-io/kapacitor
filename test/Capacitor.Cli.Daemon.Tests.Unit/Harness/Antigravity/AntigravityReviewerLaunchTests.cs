using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity.AntigravityRuntimeFakes;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The Antigravity reviewer's LAUNCH shape — the argv and environment a turn child would actually
/// receive, and the ordering <c>StartAsync</c> owes the orchestrator — asserted directly rather than
/// through a round's outcome. A round that completes proves nothing about which flags were passed:
/// a reviewer that never attempts a shell call looks identical whether or not it was granted one.
/// </summary>
public class AntigravityReviewerLaunchTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>A path, never a created directory — <see cref="AntigravityHostedAgentRuntimeFactory.BuildTurnPsi"/> is pure, so the argv
    /// assertions below run identically on every platform (the real per-launch home is POSIX-only).</summary>
    const string HomePath = "/tmp/kcap-antigravity-home";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    /// <summary>The minimum this daemon has on record — the same value the seeded record and the
    /// version seam default to, so the "meets it exactly" case is the baseline every other arm moves
    /// away from.</summary>
    const string RecordedMinimum = "1.1.10";

    /// <param name="minimum">The recorded minimum, seeded exactly as enabling the reviewer does in
    /// production (<c>DaemonRunner</c> seeds from the consent event). Null records NOTHING, which is
    /// how the no-minimum arm is reached — the gate is not configuration, so there is no value to pass
    /// for "unset".</param>
    DaemonConfig EnabledConfig(string? model = null, string? minimum = RecordedMinimum) {
        var config = new DaemonConfig {
            AntigravityPath                      = "agy",
            AntigravityModel                     = model,
            AntigravityUnattendedReviewerEnabled = true,
            Name                                 = "test-daemon",
            DaemonEpoch                          = "epoch-1",
            Store                                = Daemons.Store
        };

        if (minimum is not null)
            AntigravityHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm(minimum);

        return config;
    }

    /// <param name="serverUrl">Defaulted to a real value; blanked by the result-channel tests, which
    /// is the ONE input a hosted launch legitimately lacks and a review legitimately cannot.</param>
    /// <param name="mcpAllowlist">The review-flow DEFINITION's allowlist. Never populated for a hosted
    /// launch in production — a test passes one only to assert that the hosted arm ignores it rather
    /// than validating it.</param>
    /// <param name="mcpServers">The literal per-launch MCP server list. No production caller populates
    /// it today (see the field's own comment on <see cref="RuntimeStartContext"/>); a test passes one
    /// to pin which arm forwards it.</param>
    /// <param name="isReview">The PR-review launch kind (<c>LaunchKind.Review</c>) — a THIRD shape,
    /// distinct from both arms above and refused outright; see the test that pins it.</param>
    /// <param name="brokeredResultDelivery">Defaults to <paramref name="isReviewFlow"/>, which is
    /// exactly what the orchestrator derives for this runtime: it declares
    /// <c>ReviewFlowRedirectsHome</c>, so every review it serves is brokered and no hosted launch is.
    /// A test passes <see langword="false"/> on a review to reach the wiring guard.</param>
    /// <param name="flowResultCapabilityUrl">The loopback capability the brokered channel delivers
    /// through, minted on the reviewer grant. Defaulted to a real-shaped value because production
    /// always has one here.</param>
    static RuntimeStartContext Ctx(
            bool isReviewFlow = true, AgentActivityClock? clock = null, string? model = null,
            string? serverUrl = "http://kcap.test", string capacitorPath = "/usr/local/bin/kcap",
            string[]? mcpAllowlist = null, IReadOnlyList<AcpMcpServerSpec>? mcpServers = null,
            bool isReview = false, bool? brokeredResultDelivery = null,
            string? flowResultCapabilityUrl = CapabilityUrl) => new(
        AgentId: "agent-1", Vendor: "antigravity", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"),
        Prompt: "review this",
        Model: model, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow,
        Review: isReview ? new ReviewLaunchInfo("owner", "repo", 42) : null,
        Cols: 80, Rows: 24,
        ServerUrl: serverUrl, DaemonBridgeUrl: null, CapacitorPath: capacitorPath,
        McpAllowlist: mcpAllowlist,
        DaemonId: "daemon-1", DaemonEpoch: "epoch-1",
        McpServers: mcpServers,
        RequiresBrokeredResultDelivery: brokeredResultDelivery ?? isReviewFlow,
        FlowResultCapabilityUrl: flowResultCapabilityUrl,
        ActivityClock: clock);

    /// <summary>Shape-accurate stand-in for the reviewer grant's own URL (loopback + 32 hex chars),
    /// so nothing here passes on a value the bridge would never mint.</summary>
    const string CapabilityUrl = "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef";

    static ProcessStartInfo BuildPsi(
            DaemonConfig config, string? conversationId = null, bool isReviewFlow = true) =>
        AntigravityHostedAgentRuntimeFactory.BuildTurnPsi(
            config, Ctx(isReviewFlow: isReviewFlow), prompt: "<PROMPT>",
            conversationId: conversationId, home: HomePath);

    // ── argv ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whole-vector, joined so a failure prints both sequences: a substring check would pass a
    /// build that ADDED a flag, which is exactly the regression class this assertion exists for.</summary>
    [Test]
    public async Task The_whole_argv_vector_is_pinned() {
        var psi = BuildPsi(EnabledConfig(model: "gemini-3.5-flash"));

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo(string.Join(" ", [
            "-p", "<PROMPT>",
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--print-timeout", "600s",
            "--model", "gemini-3.5-flash"
        ]));
    }

    [Test]
    public async Task The_reviewer_never_gets_skip_permissions_or_sandbox() {
        var psi = BuildPsi(EnabledConfig());

        // The reviewer reads an OWNED worktree; agy's headless soft-deny of shell and out-of-workspace
        // operations IS the desired unattended posture. Widening it would grant shell access a
        // reviewer never needs.
        await Assert.That(psi.ArgumentList).DoesNotContain("--dangerously-skip-permissions");
        await Assert.That(psi.ArgumentList).DoesNotContain("--sandbox");
    }

    /// <summary>
    /// The hosted/reviewer asymmetry, pinned in ONE test so the two directions cannot drift apart —
    /// and as WHOLE vectors, because the interesting regression is a flag appearing on the arm that
    /// must not have it, which every substring check would pass.
    ///
    /// <para><b>What the flag actually buys, measured on agy 1.1.10:</b> with it, an absolute
    /// out-of-workspace <c>view_file</c> succeeds; without it the same read is refused with a typed
    /// <c>tool_info.error</c>. So the flag IS the read boundary — the daemon-owned worktree is not one
    /// — and a hosted agent launched without it soft-denies shell and out-of-workspace work and merely
    /// looks broken. A reviewer only reads its own worktree, so the soft-deny is the posture it
    /// wants.</para>
    ///
    /// <para>Pure builder, so both arms run on every host — no process, no filesystem.</para>
    /// </summary>
    [Test]
    public async Task A_hosted_launch_widens_permissions_but_a_reviewer_launch_does_not() {
        var config = EnabledConfig();

        var hosted   = BuildPsi(config, isReviewFlow: false);
        var reviewer = BuildPsi(config, isReviewFlow: true);

        await Assert.That(string.Join(" ", hosted.ArgumentList)).IsEqualTo(string.Join(" ", [
            "-p", "<PROMPT>",
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--dangerously-skip-permissions",
            "--print-timeout", "600s"
        ]));

        await Assert.That(string.Join(" ", reviewer.ArgumentList)).IsEqualTo(string.Join(" ", [
            "-p", "<PROMPT>",
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--print-timeout", "600s"
        ]));
    }

    [Test]
    public async Task Turn_one_does_not_pass_a_conversation_flag() {
        var psi = BuildPsi(EnabledConfig(), conversationId: null);

        await Assert.That(psi.ArgumentList).DoesNotContain("--conversation");
    }

    [Test]
    public async Task Turns_after_the_first_resume_the_same_conversation() {
        var psi = BuildPsi(EnabledConfig(), conversationId: "e80c33bf-c10f-4d2f-b626-b0043f488fc0");

        // Adjacent, not merely both present: a build that emitted the id under a different flag (or
        // the flag with a different value) would satisfy two independent Contains checks while
        // resuming nothing.
        var i = psi.ArgumentList.IndexOf("--conversation");
        await Assert.That(i).IsGreaterThanOrEqualTo(0);
        await Assert.That(psi.ArgumentList[i + 1]).IsEqualTo("e80c33bf-c10f-4d2f-b626-b0043f488fc0");
    }

    /// <summary>The per-turn ceiling agy applies to ITSELF must be the one we configured, not its own
    /// 5m default — a vendor default silently changes a bound we did not choose.</summary>
    [Test]
    public async Task The_print_timeout_is_derived_from_the_configured_turn_ceiling() {
        var config = EnabledConfig();
        config.AntigravityReviewerTurnTimeoutSeconds = 42;

        var psi = BuildPsi(config);
        var i   = psi.ArgumentList.IndexOf("--print-timeout");

        await Assert.That(i).IsGreaterThanOrEqualTo(0);
        await Assert.That(psi.ArgumentList[i + 1]).IsEqualTo("42s");
    }

    // ── env ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Daemon_identity_markers_are_stamped_on_every_turn_child() {
        var psi = BuildPsi(EnabledConfig());

        // Without these, OrphanReaper's env-marker pass cannot see a survivor of a prior incarnation
        // of this daemon — and nothing fails visibly when they are omitted.
        await Assert.That(psi.Environment["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_ID"]).IsEqualTo("daemon-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_EPOCH"]).IsEqualTo("epoch-1");
        await Assert.That(psi.Environment["KCAP_URL"]).IsEqualTo("http://kcap.test");
    }

    /// <summary>Containment: the turn child's whole HOME (and its temp tree, which agy writes into)
    /// is the per-launch isolated one. An inherited HOME would let agy's own kcap capture hooks fire
    /// against the conversation this runtime is already recording.</summary>
    [Test]
    public async Task The_turn_child_runs_under_the_isolated_home_and_a_home_local_tmpdir() {
        var psi = BuildPsi(EnabledConfig());

        await Assert.That(psi.Environment["HOME"]).IsEqualTo(HomePath);
        await Assert.That(psi.Environment["TMPDIR"]).IsEqualTo(Path.Combine(HomePath, "tmp"));
        // Named rather than left to the derivation: an operator with KCAP_CONFIG_DIR exported would
        // otherwise have agy's own kcap read the real profile through inheritance.
        await Assert.That(psi.Environment[ConfigRoot.ConfigDirEnvVar])
            .IsEqualTo(Path.Combine(HomePath, ".config", "kcap"));
    }

    /// <summary>An operator with <c>GEMINI_CLI_HOME</c> exported must not have it reach the turn
    /// child: the child derives its whole Gemini root (config, MCP servers, result channel) from the
    /// isolated <c>HOME</c> above, and an inherited override would point it at the operator's real
    /// profile instead — see <see cref="AntigravityReviewerHome"/>'s layout derivation.</summary>
    [Test]
    [NotInParallel]
    public async Task The_turn_child_does_not_inherit_a_gemini_cli_home_override() {
        const string inherited = "/tmp/kcap-gemini-cli-home-probe";
        using var _ = EnvScope.Exclusive("GEMINI_CLI_HOME", inherited);

        var psi = BuildPsi(EnabledConfig());

        // Absence is assertable only because the scope above established the value: the child's block
        // is seeded from this process, so a variable nothing set proves nothing about the scrub.
        await Assert.That(psi.Environment.ContainsKey("GEMINI_CLI_HOME")).IsFalse();
    }

    /// <summary>Google auth is INHERITED from the daemon's own environment (<c>UseShellExecute</c> is
    /// false, so the child gets the daemon's block plus our overrides). Re-stamping it here would mean
    /// a rotated ADC path resolved at daemon start rather than at spawn.</summary>
    [Test]
    public async Task Google_auth_variables_are_inherited_rather_than_restamped() {
        var psi = BuildPsi(EnabledConfig());

        await Assert.That(psi.UseShellExecute).IsFalse();
        await Assert.That(psi.Environment.ContainsKey("AGY_ADC_AUTH")).IsFalse();
        await Assert.That(psi.Environment.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS")).IsFalse();
    }

    // ── advertisement ─────────────────────────────────────────────────────────────────────────────

    /// <param name="version">Seamed on EVERY construction, never left to a real probe: the gate reads
    /// the installed <c>agy</c>'s version, so an unseamed test would resolve <see langword="null"/> on
    /// any host without the binary (CI) and refuse as <c>version_unresolved</c> — passing or failing
    /// for a reason unrelated to what it asserts. Defaults to the shipped floor.</param>
    /// <param name="posixHost">Pinned rather than inherited from the runner, so every arm of the
    /// ladder is reachable from any host. Defaulting this to the ambient OS is what reddened the
    /// Windows CI leg: the platform arm short-circuits ahead of the binary and floor arms, so
    /// <c>A_missing_binary_…</c> and <c>A_below_floor_…</c> both refused on platform and failed for a
    /// reason unrelated to what they assert. Tests that genuinely touch the POSIX-only reviewer HOME
    /// still need their own <c>Skip.Unless</c> — this seam decides the gate, not the filesystem.</param>
    static AntigravityHostedAgentRuntimeFactory Factory(
            DaemonConfig config,
            Func<ProcessStartInfo, CancellationToken, Task<IAgyTurnProcess>>? turnSource = null,
            bool binaryExists = true,
            string? version = "1.1.10",
            bool posixHost = true) =>
        new(config, NullLoggerFactory.Instance, turnSource, _ => binaryExists, _ => version, posixHost);

    /// <summary>The platform arm itself — assertable from POSIX only because the seam above exists,
    /// which is the point of having it. The reviewer's per-launch home holds review context and
    /// cannot be created owner-only on Windows, so the vendor is withheld rather than advertised with
    /// a world-readable home.</summary>
    [Test]
    public async Task A_windows_host_is_withheld_as_an_unsupported_platform() {
        var support = Factory(EnabledConfig(), posixHost: false).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).Contains("antigravity_reviewer_unsupported_platform");
    }

    [Test]
    public async Task A_consent_withheld_daemon_reports_an_operator_actionable_reason() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var support = Factory(config).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        // The reason must name the switch the operator can actually flip, not merely say "disabled".
        await Assert.That(support.WithheldReason).IsNotNull();
        await Assert.That(support.WithheldReason!).Contains("antigravity_unattended_reviewer_disabled");
        await Assert.That(support.WithheldReason!).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
    }

    /// <summary>A consenting daemon whose binary is not installed is withheld too, and says which path
    /// it looked for — the failure mode the <c>agy</c> default exists to avoid is a vendor that is
    /// silently never advertised.</summary>
    [Test]
    public async Task A_missing_binary_is_withheld_with_the_path_it_looked_for() {
        var support = Factory(EnabledConfig(), binaryExists: false).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).Contains("antigravity_reviewer_binary_missing");
        await Assert.That(support.WithheldReason!).Contains("agy");
    }

    /// <summary>Consent plus a resolvable binary is enough — there is deliberately NO credential gate
    /// (owner decision): an operator without durable auth gets a bounded, coded spawn-time failure,
    /// not a vendor that silently disappears.</summary>
    [Test]
    public async Task Consent_plus_binary_is_supported_with_no_withheld_reason() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Antigravity reviewer is POSIX-only: its per-launch home holds review context and cannot be created owner-only on Windows.");

        var support = Factory(EnabledConfig()).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsTrue();
        await Assert.That(support.WithheldReason).IsNull();
    }

    // ── the recorded minimum, at the ONE ladder that owns it ──────────────────────────────────────

    /// <summary>The minimum reaches advertisement THROUGH the factory's own gate ladder, so
    /// advertisement and the launch boundary cannot disagree about it. A build that meets it — or any
    /// later one, since the record is a minimum and not an exact match — is advertised with nothing
    /// withheld.</summary>
    [Test]
    [Arguments("1.1.10")]
    [Arguments("2.5.0")]
    public async Task A_minimum_meeting_build_is_advertised(string installed) {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var support = Factory(EnabledConfig(), version: installed).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsTrue();
        await Assert.That(support.WithheldReason).IsNull();
    }

    [Test]
    public async Task A_below_minimum_build_is_withheld_with_a_reason_naming_both_versions() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var support = Factory(EnabledConfig(), version: "1.1.8").DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).StartsWith("antigravity_reviewer_version_below_minimum");
        await Assert.That(support.WithheldReason!).Contains("1.1.8");
        await Assert.That(support.WithheldReason!).Contains(RecordedMinimum);
    }

    /// <summary>An absent record must not read as permission. This is the control for the seeding
    /// path: without it, a daemon that never seeded and a working gate look identical from here.
    /// </summary>
    [Test]
    public async Task A_daemon_with_no_recorded_minimum_is_withheld() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var support = Factory(EnabledConfig(minimum: null)).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).StartsWith("antigravity_reviewer_version_no_minimum");
    }

    /// <summary>The record is read per decision, never snapshotted at construction: `kcap daemon
    /// reviewer affirm` runs in a DIFFERENT process while this daemon is live, and a cached read would
    /// leave the operator's affirmation inert until a restart the verb does not require of them.
    /// </summary>
    [Test]
    public async Task An_affirmation_taken_under_a_running_daemon_is_picked_up() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config  = EnabledConfig(minimum: "9.9.9");
        var factory = Factory(config, version: "1.1.10");

        await Assert.That(factory.DescribeUnattendedSupport().Supported).IsFalse();

        AntigravityHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm("1.1.10");

        await Assert.That(factory.DescribeUnattendedSupport().Supported).IsTrue();
    }

    /// <summary>A probe that could not identify the build refuses under its OWN arm — the operator's
    /// next action is to fix the binary, not to upgrade it.</summary>
    [Test]
    public async Task An_unidentifiable_build_is_withheld_as_unresolved() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var support = Factory(EnabledConfig(), version: null).DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason!).StartsWith("antigravity_reviewer_version_unresolved");
    }

    /// <summary>A missing binary reports as MISSING, not as an unidentifiable version — the presence
    /// check has to precede the probe, or an operator with no <c>agy</c> at all is sent to check a
    /// version that was never going to resolve.</summary>
    [Test]
    public async Task A_missing_binary_outranks_the_version_arm() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var support = Factory(EnabledConfig(), binaryExists: false, version: null).DescribeUnattendedSupport();

        await Assert.That(support.WithheldReason!).StartsWith("antigravity_reviewer_binary_missing");
    }

    /// <summary>Consent is read FIRST and short-circuits before the probe: an installed-but-wedged
    /// <c>agy</c> must not be spawned — let alone stall a daemon start — for a feature the operator
    /// switched off. The below-floor version is what makes this a short-circuit assertion rather than
    /// a restatement of the disabled arm.</summary>
    [Test]
    public async Task A_consent_withheld_daemon_never_probes_the_binary() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var probes = 0;
        var factory = new AntigravityHostedAgentRuntimeFactory(
            config, NullLoggerFactory.Instance, turnSource: null, binaryExists: _ => true,
            resolveVersion: _ => { probes++; return "0.0.1"; });

        await Assert.That(factory.DescribeUnattendedSupport().Supported).IsFalse();
        await Assert.That(probes).IsEqualTo(0);
    }

    /// <summary>The binary is probed ONCE per decision — the verdict and its explanation both need the
    /// version, and resolving it per consumer spawns the vendor binary twice to produce one
    /// refusal — and it is probed at the path this DAEMON would launch, not whatever <c>agy</c>
    /// happens to resolve to first.</summary>
    [Test]
    public async Task The_probe_reads_the_configured_path_exactly_once() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config = EnabledConfig();
        config.AntigravityPath = "/opt/agy/bin/agy";

        var seen   = new List<string>();
        var factory = new AntigravityHostedAgentRuntimeFactory(
            config, NullLoggerFactory.Instance, turnSource: null, binaryExists: _ => true,
            resolveVersion: path => { seen.Add(path); return "1.1.8"; });

        factory.DescribeUnattendedSupport();

        await Assert.That(seen).IsEquivalentTo(new[] { "/opt/agy/bin/agy" });
    }

    [Test]
    public async Task Borrowed_review_is_not_offered() {
        // No sandbox-exec substrate here: reviews fail closed to an owned worktree, as Kiro and
        // Claude do. Read through the interface, since that is the only surface the orchestrator's
        // routing ever consults.
        IHostedAgentRuntimeFactory factory = Factory(EnabledConfig());

        await Assert.That(factory.SupportsBorrowedReviewFlow).IsFalse();
        await Assert.That(factory.BorrowedReviewContainment).IsNull();
        await Assert.That(factory.Vendor).IsEqualTo("antigravity");
    }

    // ── StartAsync ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records every spawned turn's <see cref="ProcessStartInfo"/> so a test can assert on
    /// what the SECOND turn's argv carried without a real process.</summary>
    sealed class SpyTurnSource {
        readonly FakeTurn _turn;
        public List<ProcessStartInfo> Spawns { get; } = [];

        public SpyTurnSource(FakeTurn turn = FakeTurn.Normal) => _turn = turn;

        public Task<IAgyTurnProcess> SpawnAsync(ProcessStartInfo psi, CancellationToken ct) {
            Spawns.Add(psi);
            return Task.FromResult<IAgyTurnProcess>(new FakeAgyTurnProcess(_turn, FixedConversationId));
        }
    }

    /// <summary>Waits (in small real increments — this is process synchronization, not the thing under
    /// test) until the spy has recorded <paramref name="count"/> spawns, and fails loudly rather than
    /// silently proceeding on a count that never arrived.</summary>
    static async Task SpawnedAtLeast(SpyTurnSource spy, int count) {
        var deadline = DateTime.UtcNow + HangGuard;

        while (spy.Spawns.Count < count && DateTime.UtcNow < deadline) await Task.Delay(5);

        if (spy.Spawns.Count < count)
            throw new TimeoutException($"Only {spy.Spawns.Count} turn(s) were spawned; expected {count}.");
    }

    /// <summary>
    /// The ordering the orchestrator depends on: it reads <c>transcript.AcpSessionId</c> synchronously
    /// the moment a launch returns, so a <c>StartAsync</c> that returned before turn 1's <c>init</c>
    /// was parsed would bind the transcript to <c>""</c> — a silent, permanent correlation break.
    ///
    /// <para>Also pins the clock wiring: the clock must be attached BEFORE the first turn runs, since
    /// a clock assigned later makes every stamp inside the launch a silent no-op. <c>ActivitySeq</c>
    /// starts at 1 and only a real stamp against THIS instance can move it.</para>
    /// </summary>
    [Test]
    public async Task StartAsync_returns_with_the_conversation_id_bound_and_the_clock_already_wired() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy   = new SpyTurnSource();
        var clock = new AgentActivityClock(new FakeTimeProvider());

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(clock: clock), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        await Assert.That(start.Transcript).IsNotNull();
        await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo(FixedConversationId);

        // >1 is only reachable if a stamp ran against THIS clock instance during the launch.
        await Assert.That(clock.ActivitySeq).IsGreaterThan(1UL);
    }

    /// <summary>Turn 2 must resume turn 1's conversation, or every round lands as its own kcap
    /// session. Asserted on the SECOND spawn's argv — the id is read back from the launch rather than
    /// reconstructed, so this cannot pass against a launch that resumed something else.</summary>
    [Test]
    public async Task A_second_turn_resumes_the_conversation_the_first_turn_established() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy   = new SpyTurnSource();
        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        await runtime.SendUserInputAsync("round 2").WaitAsync(HangGuard);

        // Polled, not WaitForTurnIdleAsync: that call's acquire-then-release can complete against a
        // momentarily-free gate before the worker has even dequeued the turn, so it is not a "the
        // turn ran" barrier (see the runtime's own class doc). This waits for the observable fact.
        await SpawnedAtLeast(spy, 2);
        await Assert.That(spy.Spawns[0].ArgumentList).DoesNotContain("--conversation");

        var second = spy.Spawns[1].ArgumentList;
        var i      = second.IndexOf("--conversation");
        await Assert.That(i).IsGreaterThanOrEqualTo(0);
        await Assert.That(second[i + 1]).IsEqualTo(start.Transcript!.AcpSessionId);
    }

    /// <summary>
    /// An interactive (hosted) launch runs — it is not refused — and it runs under the SAME per-launch
    /// isolated home the reviewer gets, with the widened permission flag the reviewer never gets.
    ///
    /// <para>Both halves are measured facts rather than preferences. The refusal this replaces claimed
    /// an inherited HOME would let agy's capture hooks spawn a watcher that holds the turn's stdout
    /// open; four piped runs under a real HOME, on a kcap predating the descriptor fix, all reached
    /// EOF in 6–16s, so the wedge does not reproduce. What those runs DID show is that each was also
    /// recorded by the hook lane as its own watcher session — and this runtime is already the
    /// transcript source, so an inherited HOME would not add capture, it would DUPLICATE it. Isolation
    /// is the fix here, not a cost, which is why the hosted arm keeps it.</para>
    /// </summary>
    [Test]
    public async Task An_interactive_launch_is_hosted_under_the_isolated_home_with_permissions_widened() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config = EnabledConfig();
        var spy    = new SpyTurnSource();

        var start = await Factory(config, spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        // The launch completed and bound a conversation — the same contract a review launch owes.
        await Assert.That(start.Transcript).IsNotNull();
        await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo(FixedConversationId);

        // Positive containment assertion, not an inequality against the ambient HOME: the child's home
        // is one this daemon created under its OWN state root, and is therefore removed with it.
        await Assert.That(spy.Spawns[0].Environment["HOME"]!)
            .StartsWith(AntigravityHostedAgentRuntimeFactory.ReviewerStateDir(config));

        // The widened argv reaches the real spawn path, not merely the pure builder.
        await Assert.That(spy.Spawns[0].ArgumentList).Contains("--dangerously-skip-permissions");
    }

    /// <summary>
    /// <b>The split this ladder exists for, asserted on ONE daemon so it is a split and not two
    /// independent facts.</b> The consent flag is REVIEWER-ONLY, and its own justification is what
    /// makes that so: an unattended review runs under the daemon user's authority and returns what it
    /// read to whoever requested the review, who need not be the operator. A hosted launch has no such
    /// exposure — the server's <c>DaemonRegistry</c> is keyed
    /// <c>(TeamId, OwnerUserId, Name)</c> and the launch hub resolves a daemon with the CALLER's own
    /// normalized user id, so the launcher IS the daemon's owner — and hosted Antigravity therefore
    /// ships on by default, like every other hosted vendor.
    ///
    /// <para>Before this, a hosted launch on a daemon that had simply never set the reviewer flag
    /// failed with <c>antigravity_unattended_reviewer_disabled</c> — a review complaint about a launch
    /// that is not a review.</para>
    /// </summary>
    [Test]
    public async Task Consent_gates_a_review_launch_but_not_a_hosted_launch_on_the_same_daemon() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var factory = Factory(config, new SpyTurnSource().SpawnAsync);

        // Hosted: runs. Reading the bound conversation id (not merely "did not throw") is what makes
        // this a launch assertion rather than a gate assertion.
        var start = await factory.StartAsync(Ctx(isReviewFlow: false), CancellationToken.None)
            .WaitAsync(HangGuard);

        await using (var runtime = start.Runtime)
            await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo(FixedConversationId);

        // Review: still refused, on the SAME factory instance, with the text that names the switch.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_unattended_reviewer_disabled");
        await Assert.That(ex.Message).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
    }

    /// <summary>Defence in depth: the orchestrator's unattended gate runs first, but an explicit
    /// vendor request can reach a factory directly, so the consent gate is re-applied at the launch
    /// boundary rather than trusted to advertisement. Separate from the split test above because this
    /// arm throws before touching the filesystem, so it is assertable on Windows too.</summary>
    [Test]
    public async Task A_consent_withheld_daemon_refuses_a_review_launch() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(config, new SpyTurnSource().SpawnAsync)
                .StartAsync(Ctx(), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_unattended_reviewer_disabled");
    }

    /// <summary>
    /// <b>The arm most likely to be lost by an over-broad "hosted skips the ladder" fix.</b> The build
    /// minimum is not reviewer-specific: it protects the per-launch isolated <c>HOME</c>, which is
    /// containment a hosted launch depends on exactly as a review does — losing it silently removes
    /// that protection.
    ///
    /// <para>Asserted on a CONSENT-LESS daemon deliberately. On a consenting one the refusal could not
    /// distinguish "the floor applies to hosted" from "the ladder still runs at all", and a fix that
    /// skipped the whole ladder for hosted launches would pass.</para>
    /// </summary>
    [Test]
    public async Task A_below_minimum_build_is_refused_for_a_hosted_launch_too() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(config, new SpyTurnSource().SpawnAsync, version: "1.1.8")
                .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_version_below_minimum");
    }

    /// <summary>The platform arm is shared for the same reason as the floor: Windows cannot create the
    /// per-launch home owner-only, and that home holds a hosted agent's own conversation transcript as
    /// surely as it holds a reviewer's.</summary>
    [Test]
    public async Task A_windows_host_refuses_a_hosted_launch_too() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(config, new SpyTurnSource().SpawnAsync, posixHost: false)
                .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_unsupported_platform");
    }

    /// <summary>Binary presence is shared too — there is nothing to launch either way.</summary>
    [Test]
    public async Task A_missing_binary_refuses_a_hosted_launch_too() {
        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(config, new SpyTurnSource().SpawnAsync, binaryExists: false)
                .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_binary_missing");
    }

    /// <summary>
    /// The floor's "nothing recorded" arm reaches a hosted launch, and its remedy must not send a
    /// hosted operator to the REVIEWER consent flag. That switch has no bearing on a hosted launch, and
    /// telling someone to set it is the same confusion — one layer down — that this whole split
    /// removes. The affirm verb is the remedy that works for both.
    /// </summary>
    [Test]
    public async Task A_hosted_launch_with_no_recorded_minimum_is_refused_without_naming_the_reviewer_flag() {
        var config = EnabledConfig(minimum: null);
        config.AntigravityUnattendedReviewerEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(config, new SpyTurnSource().SpawnAsync)
                .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_version_no_minimum");
        await Assert.That(ex.Message).DoesNotContain("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
        await Assert.That(ex.Message).Contains("kcap daemon reviewer affirm");
    }

    /// <summary>Advertisement is specifically an offer to REVIEW unattended, so it keeps the full
    /// ladder — consent included. Lifting consent from the launch boundary must not lift it here: the
    /// server refuses an unadvertised reviewer, and a daemon that advertised without consent would be
    /// offering the very thing its operator never opted into.</summary>
    [Test]
    public async Task Advertisement_still_requires_consent_even_though_a_hosted_launch_does_not() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config = EnabledConfig();
        config.AntigravityUnattendedReviewerEnabled = false;

        var factory = Factory(config, new SpyTurnSource().SpawnAsync);

        // Withheld from advertisement...
        await Assert.That(factory.SupportsUnattended).IsFalse();
        await Assert.That(factory.DescribeUnattendedSupport().WithheldReason!)
            .StartsWith("antigravity_unattended_reviewer_disabled");

        // ...on the very daemon whose hosted launches this same factory admits.
        var start = await factory.StartAsync(Ctx(isReviewFlow: false), CancellationToken.None)
            .WaitAsync(HangGuard);

        await start.Runtime.DisposeAsync();
    }

    /// <summary>A borrowed workspace is refused because this runtime has no containment strategy for
    /// one — a fact about the runtime, not about reviews — so the refusal must not describe the launch
    /// as a review. Reachable from the hosted arm, which is exactly where the old wording read wrong.
    /// </summary>
    [Test]
    public async Task A_borrowed_workspace_is_refused_without_calling_the_launch_a_review() {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(EnabledConfig(), new SpyTurnSource().SpawnAsync)
                .StartAsync(Ctx(isReviewFlow: false) with { Work = WorkLocation.BorrowedCwd },
                            CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_requires_owned_worktree");
        await Assert.That(ex.Message).Contains("daemon-owned worktree");
        await Assert.That(ex.Message).DoesNotContain("a review must run");
    }

    // ── the injected MCP surface ───────────────────────────────────────────────────────────────────
    //
    // The per-launch home's mcp_config.json IS the launch's whole MCP surface (the home carries no
    // operator config to merge with), so these assertions read that file rather than a builder's
    // return value — the same file agy would load.

    /// <summary>Reads the mcp_config.json the launch actually wrote, from the home the child was
    /// handed. Literal path rather than <c>AntigravityPaths.McpConfigJson</c>: that resolver honours
    /// the TEST PROCESS's own <c>GEMINI_CLI_HOME</c>, so it could read somewhere the launch never
    /// wrote.</summary>
    static async Task<string> McpConfigOf(SpyTurnSource spy) {
        var home = spy.Spawns[0].Environment["HOME"]!;
        var path = Path.Combine(home, ".gemini", "config", "mcp_config.json");

        if (!File.Exists(path))
            throw new FileNotFoundException($"The launch wrote no mcp_config.json under '{home}'.", path);

        // Shared read: this file lives in the child's own config dir, so agy may rewrite it. A
        // write-denying open is mandatory sharing on Windows and invisible on macOS/Linux.
        return await File.ReadAllTextSharedAsync(path);
    }

    /// <summary>
    /// <b>A hosted agent has no flow to report a result to</b>, so it gets neither the
    /// <c>kcap-flow-result</c> channel nor the fail-closed validation of that channel's inputs. Both
    /// halves matter, and the second is what this test is shaped around: the launch runs with a BLANK
    /// server url — the exact input the review-side guard rejects — because a hosted launch refused
    /// with <c>antigravity_reviewer_result_channel_incomplete</c> would be refused for a channel it
    /// never needed.
    ///
    /// <para>The config file must still EXIST and be valid: its presence is what replaces the
    /// operator's global config with an empty surface, which is the same mechanism that keeps a
    /// reviewer from starting a nested flow. Absent, agy would be free to fall back.</para>
    /// </summary>
    [Test]
    public async Task A_hosted_launch_gets_no_result_channel_and_is_not_refused_for_missing_one() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: false, serverUrl: null, capacitorPath: ""), CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        // It launched — bound conversation, not merely "did not throw".
        await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo(FixedConversationId);

        // Present, valid, and EMPTY — no result channel smuggled in under any name.
        await Assert.That(await McpConfigOf(spy)).IsEqualTo("""{"mcpServers":{}}""");
    }

    /// <summary>
    /// The review direction of the same split, and the harder one to lose safely: a reviewer with no
    /// result channel starts, runs, and can never report — the flow then waits for a verdict that
    /// cannot arrive. Asserts the channel's COMMAND and both env vars, not just its name: the server
    /// exits when <c>KCAP_FLOW_AGENT_ID</c> is absent, so a name-only assertion would pass a channel
    /// that dies on first use.
    ///
    /// <para>Also pins that the review arm builds its OWN list rather than forwarding
    /// <c>ctx.McpServers</c> — a caller-supplied server must not join a reviewer's surface.</para>
    /// </summary>
    [Test]
    public async Task A_review_launch_injects_the_flow_result_channel_and_ignores_caller_supplied_servers() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: true,
                            mcpServers: [new AcpMcpServerSpec("caller-supplied", "/bin/false", null, null)]),
                        CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        var config = await McpConfigOf(spy);

        await Assert.That(config).Contains("\"kcap-flow-result\"");
        await Assert.That(config).Contains("\"/usr/local/bin/kcap\"");
        await Assert.That(config).Contains("\"KCAP_FLOW_AGENT_ID\"");
        await Assert.That(config).Contains("\"agent-1\"");
        await Assert.That(config).Contains("\"KCAP_FLOW_CAPABILITY_URL\"");
        await Assert.That(config).DoesNotContain("caller-supplied");
    }

    /// <summary>
    /// <b>The delivery credential, at the launch boundary.</b> This runtime hands the child a
    /// per-launch isolated <c>HOME</c>, and the result channel is spawned as that child's child — so
    /// it inherits the isolated home and the config root stamped with it, an empty directory.
    /// Measured live: the channel failed with <c>Not logged in. Run 'kcap login' on the host shell.</c>
    /// after the reviewer had already produced its answer.
    ///
    /// <para>The ABSENCE of <c>KCAP_URL</c> on the channel is the load-bearing half: that variable is
    /// what routes delivery at the unreachable token store, so leaving it alongside the capability
    /// would keep the broken path reachable as a silent fallback. Asserted on the mcp_config the
    /// launch actually WROTE, not on a builder's return value — the file the child loads is the only
    /// thing that decides this.</para>
    /// </summary>
    [Test]
    public async Task A_review_launch_delivers_through_the_capability_and_never_the_token_store() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: true), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        var config = await McpConfigOf(spy);

        await Assert.That(config).Contains($"\"KCAP_FLOW_CAPABILITY_URL\":\"{CapabilityUrl}\"");
        await Assert.That(config).DoesNotContain("KCAP_URL");
    }

    /// <summary>
    /// The wiring guard, in the shape of <c>AcpHostedAgentRuntimeFactory.ValidateBorrowedArtifact</c>:
    /// this runtime DECLARES that every review it serves redirects HOME, so a review reaching it
    /// without the brokered capability means the orchestrator did not honour that declaration. The
    /// alternative to failing here is a reviewer that starts, reads, reasons and then silently cannot
    /// report — which burns the round's whole timeout and is the exact failure this whole path exists
    /// to remove.
    /// </summary>
    [Test]
    public async Task A_review_launch_that_was_not_given_brokered_delivery_is_refused() {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(EnabledConfig(), new SpyTurnSource().SpawnAsync)
                .StartAsync(Ctx(isReviewFlow: true, brokeredResultDelivery: false),
                            CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_result_delivery_unbrokered");
    }

    /// <summary>
    /// The declaration itself, read through the interface — the only surface the orchestrator
    /// consults. It is deliberately NOT borrowed-ness: this runtime refuses a borrowed workspace
    /// outright (see above) and still redirects HOME, which is precisely why keying delivery on a
    /// borrowed snapshot left this reviewer unable to report.
    /// </summary>
    [Test]
    public async Task The_runtime_declares_that_its_reviews_redirect_home() {
        IHostedAgentRuntimeFactory factory = Factory(EnabledConfig());

        await Assert.That(factory.ReviewFlowRedirectsHome).IsTrue();
        await Assert.That(factory.SupportsBorrowedReviewFlow).IsFalse();
    }

    /// <summary>
    /// <b>The THIRD launch shape is refused, not silently degraded.</b> Everything hosted here keys off
    /// <c>!ctx.IsReviewFlow</c>, which is true for <c>LaunchKind.Default</c> AND
    /// <c>LaunchKind.Review</c> — so a PR-review launch would otherwise take the hosted arm and run
    /// with <c>--dangerously-skip-permissions</c>, an EMPTY MCP surface (no <c>kcap mcp review</c>
    /// tools) and no review system prompt: only <c>PtyHostedAgentRuntimeFactory</c> builds
    /// <c>LauncherContext.ReviewLaunch</c>, so nothing on this path can supply them. A PR-review agent
    /// with none of its review tools and no error is worse than no PR-review agent.
    ///
    /// <para>Refused BEFORE the containment ladder deliberately: no amount of installing, affirming or
    /// consenting can make this shape work, so the operator should read that rather than "install
    /// agy". The shipped UI cannot request it today (the server's
    /// <c>AgentStoreDataService.RequestLaunchReviewAgentAsync</c> hard-codes Claude, and
    /// <c>ReviewPrDialog</c> has no vendor picker) — but the hub's <c>RequestLaunchAgent</c> takes
    /// <c>vendor</c> and <c>kind</c> as independent client-supplied arguments and rejects only
    /// Codex+Review, so this is a wire-reachable shape, not merely a latent one.</para>
    /// </summary>
    [Test]
    public async Task A_pr_review_launch_is_refused_rather_than_taking_the_hosted_arm() {
        var spy = new SpyTurnSource();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(EnabledConfig(), spy.SpawnAsync)
                .StartAsync(Ctx(isReviewFlow: false, isReview: true), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_pr_review_unsupported");

        // Refused, not merely reported: nothing was spawned.
        await Assert.That(spy.Spawns.Count).IsEqualTo(0);
    }

    /// <summary>The negative control for the refusal above: an ordinary hosted launch shares its
    /// <c>!IsReviewFlow</c> arm, so a guard written against the wrong predicate would refuse every
    /// interactive agent — and this vendor's whole hosted lane with it.</summary>
    [Test]
    public async Task An_ordinary_hosted_launch_is_not_refused_as_a_pr_review() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: false), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        await Assert.That(spy.Spawns.Count).IsGreaterThan(0);
    }

    /// <summary>A review whose result-channel inputs are incomplete must STILL fail closed with the
    /// coded reason — scoping the injection to review flows must not weaken it. Blank server url only:
    /// the guard is an OR over three inputs, and blanking one is what proves it still runs.</summary>
    [Test]
    public async Task A_review_launch_missing_result_channel_inputs_still_fails_closed() {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(EnabledConfig(), new SpyTurnSource().SpawnAsync)
                .StartAsync(Ctx(isReviewFlow: true, serverUrl: null), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_result_channel_incomplete");
    }

    /// <summary>The allowlist is the review-flow DEFINITION's, so its rejection is review-only too —
    /// asserted as a split on one factory, because a hosted launch refused for a definition it has no
    /// definition for is the same category error as the result-channel refusal above.</summary>
    [Test]
    public async Task A_non_auto_approvable_allowlist_refuses_a_review_but_not_a_hosted_launch() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy     = new SpyTurnSource();
        var factory = Factory(EnabledConfig(), spy.SpawnAsync);

        // kcap-flows starts flows — the recursion the reviewer allowlist exists to refuse.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(isReviewFlow: true, mcpAllowlist: ["kcap-flows"]), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_mcp_allowlist_rejected");

        // The same allowlist on a hosted launch is simply not its concern — and is never materialized.
        var start = await factory
            .StartAsync(Ctx(isReviewFlow: false, mcpAllowlist: ["kcap-flows"]), CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        await Assert.That(await McpConfigOf(spy)).DoesNotContain("kcap-flows");
    }

    /// <summary>An auto-approvable allowlist entry reaches the reviewer's surface alongside the result
    /// channel. Without this the allowlist could be resolved and then dropped, and every assertion
    /// above would still pass.</summary>
    [Test]
    public async Task An_auto_approvable_allowlist_entry_reaches_the_reviewers_surface() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: true, mcpAllowlist: ["kcap-review"]), CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        var config = await McpConfigOf(spy);

        await Assert.That(config).Contains("\"kcap-review\"");
        await Assert.That(config).Contains("\"kcap-flow-result\"");
    }

    // ── the launch's permissions.allow ─────────────────────────────────────────────────────────────
    //
    // Injecting the result channel is only half of a working reviewer. `agy -p` auto-denies every tool
    // confirmation it raises, and the channel IS an MCP tool — so a launch that injects it and grants
    // nothing produces a reviewer that reads its context, reasons, and can never report. Measured: the
    // conversation stops at PLANNER_RESPONSE with no TOOL_CALL and the round hangs to the flow's
    // timeout. The mcp_config assertions above all pass in exactly that state.

    /// <summary>Reads the settings.json the launch actually wrote, from the home the child was handed.
    /// Literal path, for the same reason <see cref="McpConfigOf"/> uses one.</summary>
    static async Task<string?> SettingsOf(SpyTurnSource spy) {
        var path = Path.Combine(spy.Spawns[0].Environment["HOME"]!, ".gemini", "antigravity-cli", "settings.json");

        return File.Exists(path) ? await File.ReadAllTextSharedAsync(path) : null;
    }

    /// <summary>The delivery half of a review launch, asserted at the launch boundary rather than only
    /// on the home builder — the grant has to reach the same HOME the child is spawned under, and
    /// nothing else in this file would notice if it did not.</summary>
    [Test]
    public async Task A_review_launch_grants_its_own_result_channel() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: true), CancellationToken.None).WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        var settings = await SettingsOf(spy);

        await Assert.That(settings).IsNotNull();
        await Assert.That(settings!).Contains("mcp(kcap-flow-result/submit_review_result)");

        // Never the blunt instrument: a wildcard would satisfy the assertion above while granting
        // whatever the channel serves next, and --dangerously-skip-permissions stays off this arm.
        await Assert.That(settings).DoesNotContain("*");
        await Assert.That(spy.Spawns[0].ArgumentList).DoesNotContain("--dangerously-skip-permissions");
    }

    /// <summary>The other half of the split: a hosted launch already runs with
    /// <c>--dangerously-skip-permissions</c>, so a grant would be dead config — and its injected
    /// servers are a caller's, which the reviewer classifier refuses outright.</summary>
    [Test]
    public async Task A_hosted_launch_is_granted_nothing_and_is_not_refused_for_a_caller_supplied_server() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: false,
                            mcpServers: [new AcpMcpServerSpec("caller-supplied", "/bin/echo", ["hi"], null)]),
                        CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        await Assert.That(await SettingsOf(spy)).IsNull();
        await Assert.That(spy.Spawns[0].ArgumentList).Contains("--dangerously-skip-permissions");
    }

    /// <summary>The hosted arm forwards <c>ctx.McpServers</c> verbatim, mirroring
    /// <c>AcpHostedAgentRuntimeFactory</c>'s hosted arm. <b>No production caller populates that field
    /// today</b>, so this pins a contract rather than a live path — the point being that a hosted agy
    /// launch ends up with nothing DELIBERATELY (nothing is offered) rather than by a drop this
    /// change introduced.</summary>
    [Test]
    public async Task A_hosted_launch_forwards_the_mcp_servers_it_was_given() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy = new SpyTurnSource();

        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(isReviewFlow: false,
                            mcpServers: [new AcpMcpServerSpec("caller-supplied", "/bin/echo", ["hi"], null)]),
                        CancellationToken.None)
            .WaitAsync(HangGuard);

        await using var runtime = start.Runtime;

        var config = await McpConfigOf(spy);

        await Assert.That(config).Contains("\"caller-supplied\"");
        await Assert.That(config).DoesNotContain("kcap-flow-result");
    }

    /// <summary>
    /// The same defence in depth for the recorded MINIMUM, which is the gap a check applied only at
    /// advertisement leaves: advertisement is what stops a launch being attempted, but an explicit
    /// <c>vendor: "antigravity"</c> request reaches this factory without consulting it, so a
    /// below-minimum build could be launched. One ladder, read at both seams, closes it.
    /// </summary>
    [Test]
    public async Task A_below_minimum_build_is_refused_at_the_launch_boundary_too() {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(EnabledConfig(), new SpyTurnSource().SpawnAsync, version: "1.1.8")
                .StartAsync(Ctx(), CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_version_below_minimum");
    }

    /// <summary>
    /// The measured unauthenticated shapes: an immediate `authentication required` error, or an OAuth
    /// URL followed by an interactive wait. Either way the child says so and produces no <c>init</c>,
    /// so the launch must fail with the coded, non-retryable reason NAMING the ADC remedy — a generic
    /// launch failure would send an operator looking at the wrong thing.
    /// </summary>
    [Test]
    public async Task An_unauthenticated_first_turn_fails_with_the_coded_adc_remedy() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var factory = Factory(EnabledConfig(), (_, _) => Task.FromResult<IAgyTurnProcess>(
            new UnauthenticatedTurnProcess()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_auth_unavailable");

        // ALL THREE, and the third is the one that regressed: an earlier revision named only the
        // switch and the project, and an operator following it exactly still got
        // `authentication required` — because ADC's default location is under the HOME this launch
        // redirects, and neither of those two carries a path. A remedy that leaves the operator
        // where they started is worse than no remedy, so the message is pinned, not just its code.
        await Assert.That(ex.Message).Contains("AGY_ADC_AUTH");
        await Assert.That(ex.Message).Contains("GOOGLE_CLOUD_PROJECT");
        await Assert.That(ex.Message).Contains("GOOGLE_APPLICATION_CREDENTIALS");

        // And it must say WHY the path is needed despite ADC having a default — without that an
        // operator reasonably deletes it as redundant.
        await Assert.That(ex.Message).Contains("HOME");
    }

    /// <summary>
    /// The control for the test above, and it must carry NON-EMPTY output that simply is not an auth
    /// signal. An earlier revision used a child that said nothing at all — a mutation check showed
    /// that made the assertion vacuous, because a null-diagnostics child is rejected by the pattern
    /// match before the classifier is ever consulted, so a classifier hard-wired to <c>true</c>
    /// survived. With real, unrelated output the classifier is the guard that decides.
    /// </summary>
    [Test]
    public async Task A_first_turn_that_dies_with_unrelated_output_is_not_reported_as_an_auth_failure() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var factory = Factory(EnabledConfig(), (_, _) => Task.FromResult<IAgyTurnProcess>(
            new DyingTurnProcess("panic: runtime error: index out of range [3]")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_launch_failed");
    }

    /// <summary>And a child that says nothing at all is likewise not an auth failure — the arm the
    /// pattern match, rather than the classifier, is responsible for.</summary>
    [Test]
    public async Task A_first_turn_that_dies_silently_is_not_reported_as_an_auth_failure() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var factory = Factory(EnabledConfig(), (_, _) => Task.FromResult<IAgyTurnProcess>(
            new DyingTurnProcess(null)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_launch_failed");
    }

    /// <summary>
    /// A child that is ALIVE and never says anything — the production shape, and the one the two
    /// terminating fixtures above structurally cannot produce. Measured, an unauthenticated agy can
    /// print an OAuth URL and wait ~60s for a paste that will never come; without an absolute bound
    /// the launch would sit there, and <c>ReadOutputAsync</c> is parked by design so nothing else
    /// would ever complete. Asserts the child was REAPED, not merely that an error was thrown:
    /// abandoning it would leave a reviewer holding a daemon slot the server has already given up on.
    /// </summary>
    [Test]
    public async Task An_alive_but_silent_first_turn_hits_the_launch_deadline_and_is_reaped() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var config = EnabledConfig();
        config.AntigravityReviewerLaunchTimeoutSeconds = 1;

        var child   = new AliveSilentTurnProcess();
        var factory = Factory(config, (_, _) => Task.FromResult<IAgyTurnProcess>(child));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_launch_timeout");
        await Assert.That(child.Terminated).IsTrue();
    }

    /// <summary>A daemon shutdown mid-launch propagates AS a cancellation, not as a coded reviewer
    /// fault — otherwise every agent in flight when the daemon stopped leaves a launch-failure record
    /// describing a problem that never happened.</summary>
    [Test]
    public async Task A_shutdown_mid_launch_surfaces_as_cancellation_not_a_reviewer_fault() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var child   = new AliveSilentTurnProcess();
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = Factory(EnabledConfig(), (_, _) => {
            spawned.TrySetResult();
            return Task.FromResult<IAgyTurnProcess>(child);
        });

        using var cts = new CancellationTokenSource();
        var launch    = factory.StartAsync(Ctx(), cts.Token);

        // Cancel only once the child genuinely exists. Cancelling earlier is a DIFFERENT (also
        // correct) path — the turn is dropped without ever spawning — and asserting the reap below
        // would then be judging a child that was never created.
        await spawned.Task.WaitAsync(HangGuard);
        await cts.CancelAsync();

        // Assignable, not exact. Which cancellation type surfaces depends on WHERE the cancel is
        // observed — `OperationCanceledException` from a token check, `TaskCanceledException` from an
        // awaited task — and both are correct here. `Assert.ThrowsAsync<T>` matches the type exactly,
        // so it made this assertion load-dependent: it passed under the whole-suite filter and failed
        // deterministically when this class ran alone.
        Exception? caught = null;
        try { await launch.WaitAsync(HangGuard); } catch (Exception ex) { caught = ex; }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!).IsAssignableTo<OperationCanceledException>();

        // Still reaped: a shutdown must not be the one path that leaks a child.
        await Assert.That(child.Terminated).IsTrue();
    }

    /// <summary>
    /// The per-launch home carries the reviewer's own conversation JSONL — the caller's diff, source
    /// excerpts and findings — so its removal is disposal, not disk hygiene. The daemon-epoch sweep is
    /// a crash backstop, not the disposal path: without this the home survives every completed review
    /// until the next daemon boot.
    /// </summary>
    [Test]
    public async Task The_reviewer_home_is_removed_when_the_runtime_is_disposed() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        var spy   = new SpyTurnSource();
        var start = await Factory(EnabledConfig(), spy.SpawnAsync)
            .StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard);

        var home = spy.Spawns[0].Environment["HOME"]!;
        await Assert.That(Directory.Exists(home)).IsTrue();

        await start.Runtime.DisposeAsync();

        await Assert.That(Directory.Exists(home)).IsFalse();
    }

    /// <summary>A launch that fails must not leave review context behind either — the failed-launch
    /// path is the one where nobody downstream will ever call <c>DisposeAsync</c> for us.</summary>
    [Test]
    public async Task A_failed_launch_removes_the_reviewer_home_it_created() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX-only: the per-launch home cannot be owner-only on Windows.");

        string? home    = null;
        var     factory = Factory(EnabledConfig(), (psi, _) => {
            home = psi.Environment["HOME"];
            return Task.FromResult<IAgyTurnProcess>(new DyingTurnProcess(null));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.StartAsync(Ctx(), CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(home).IsNotNull();
        await Assert.That(Directory.Exists(home!)).IsFalse();
    }

    /// <summary>A turn child that prints an auth error and exits — no <c>init</c>, so the conversation
    /// barrier faults and the factory classifies the failure from the captured diagnostics.</summary>
    sealed class UnauthenticatedTurnProcess : IAgyTurnProcess, IAgyTurnDiagnostics {
        public int  Pid       => 4243;
        public bool HasExited => true;
        public int? ExitCode  => 1;

        public string? Diagnostics => "Error: authentication required. Run 'agy' to log in, then retry.";

#pragma warning disable CS1998 // an async iterator that yields nothing still needs the async modifier
        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null)   => Task.CompletedTask;
        public ValueTask DisposeAsync()                        => ValueTask.CompletedTask;
    }

    /// <summary>A turn child that is up, with an open pipe, and never emits a line — it ends only when
    /// something cancels or kills it.</summary>
    sealed class AliveSilentTurnProcess : IAgyTurnProcess {
        public int  Pid        => 4245;
        public bool HasExited  { get; private set; }
        public int? ExitCode   { get; private set; }
        public bool Terminated { get; private set; }

        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break;
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            Terminated = true;
            HasExited  = true;
            ExitCode ??= -1;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The same shape with caller-chosen diagnostics — the control that keeps the auth
    /// classifier from being a constant.</summary>
    sealed class DyingTurnProcess(string? diagnostics) : IAgyTurnProcess, IAgyTurnDiagnostics {
        public int  Pid       => 4244;
        public bool HasExited => true;
        public int? ExitCode  => 1;

        public string? Diagnostics => diagnostics;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null)   => Task.CompletedTask;
        public ValueTask DisposeAsync()                        => ValueTask.CompletedTask;
    }


}
