using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Pure classifier rows for the flow's daemon-install ladder: every ambiguous
/// state fails closed to attention with a coded reason — never guessed into an install/start.</summary>
public class EnsureClassifierTests {
    [Test]
    public async Task Unknown_probe_fails_closed_before_anything_else() {
        var d = EnsureClassifier.Classify(LabelProbe.Unknown, ServiceState.NotInstalled, unitPresent: false, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("status_unknown");
    }

    [Test]
    public async Task No_unit_is_install() {
        var d = EnsureClassifier.Classify(LabelProbe.Absent, ServiceState.NotInstalled, unitPresent: false, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Install);
    }

    [Test]
    public async Task Unit_present_but_stopped_is_start() {
        var d = EnsureClassifier.Classify(LabelProbe.Absent, ServiceState.Installed, unitPresent: true, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Start);
    }

    // launchd's real stopped-but-installed shape: a present unit reads state NotInstalled when the
    // probe is not Loaded — the classifier must still choose Start, not Install.
    [Test]
    public async Task Launchd_stopped_but_installed_shape_is_start() {
        var d = EnsureClassifier.Classify(LabelProbe.Absent, ServiceState.NotInstalled, unitPresent: true, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Start);
    }

    [Test]
    public async Task Running_with_validated_pid_is_already_enabled() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.AlreadyEnabled);
    }

    // A stale marker must fail closed even when the daemon is demonstrably running — the docs state
    // the fail-closed rule unqualified, so the repair rows precede the success arm.
    [Test]
    public async Task Stale_marker_precedes_a_validated_running_daemon() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: true, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("stale_marker");
    }

    // A loaded label whose unit file has disappeared — even one reading Running — is an installation
    // that cannot survive a relaunch: orphaned, never "already enabled".
    [Test]
    public async Task Running_label_without_a_unit_file_is_orphan() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: false, daemonPid: 42, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("orphan_label");
    }

    // launchd reports the job pid, so already-enabled additionally requires the running job to OWN
    // the validated daemon pid (the same ownership predicate ServiceVerify.IsReadyAsync uses).
    [Test]
    public async Task Launchd_running_with_job_pid_matching_the_validated_daemon_is_already_enabled() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: false, jobPid: 42, jobPidAvailable: true);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.AlreadyEnabled);
    }

    [Test]
    public async Task Launchd_running_with_a_different_job_pid_is_unconfirmed() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: false, jobPid: 43, jobPidAvailable: true);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("running_unconfirmed");
    }

    [Test]
    public async Task Launchd_running_without_a_job_pid_is_unconfirmed() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: false, jobPid: null, jobPidAvailable: true);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("running_unconfirmed");
    }

    // systemd/Windows cannot supply a job pid — their documented verified:false already-enabled arm
    // keeps the coarser Running + validated-daemon check.
    [Test]
    public async Task Managers_without_a_job_pid_keep_the_coarser_already_enabled_check() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: false, jobPid: null, jobPidAvailable: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.AlreadyEnabled);
    }

    [Test]
    public async Task Running_without_a_validated_pid_is_unconfirmed_attention() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("running_unconfirmed");
    }

    [Test]
    public async Task Installed_state_without_unit_is_orphan_attention() {
        var d = EnsureClassifier.Classify(LabelProbe.Absent, ServiceState.Installed, unitPresent: false, daemonPid: null, txnMarker: false, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("orphan_label");
    }

    [Test]
    public async Task Stale_marker_precedes_mutation_rows() {
        var d = EnsureClassifier.Classify(LabelProbe.Absent, ServiceState.NotInstalled, unitPresent: false, daemonPid: null, txnMarker: true, txnActive: false);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("stale_marker");
    }

    [Test]
    public async Task Active_transaction_is_never_mutated_into() {
        var d = EnsureClassifier.Classify(LabelProbe.Loaded, ServiceState.Running, unitPresent: true, daemonPid: 42, txnMarker: false, txnActive: true);
        await Assert.That(d.Action).IsEqualTo(EnsureAction.Attention);
        await Assert.That(d.Reason).IsEqualTo("txn_active");
    }
}

/// <summary>Born-prompt baking: the unit env for an ensure install forces the seed directive to
/// <c>prompt</c> and pins the expected server, regardless of what the installing shell exported.</summary>
public class EnsureUnitEnvTests {
    [Test]
    public async Task Bakes_prompt_and_expected_server() {
        var env = DaemonServiceCommands.EnsureUnitEnv("acme", "https://s.example", new Dictionary<string, string>());
        await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("prompt");
        await Assert.That(env["KCAP_EXPECT_SERVER_URL"]).IsEqualTo("https://s.example");
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("acme");
    }

    [Test]
    public async Task Prompt_wins_over_an_ambient_refusal() {
        var env = DaemonServiceCommands.EnsureUnitEnv("acme", "https://s.example",
            new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "deny" });
        await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("prompt");
    }

    [Test]
    public async Task Carries_ambient_values_that_are_not_overlaid() {
        var env = DaemonServiceCommands.EnsureUnitEnv("acme", "https://s.example",
            new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_CODEX_PATH"] = "/x/codex" });
        await Assert.That(env["PATH"]).IsEqualTo("/usr/bin");
        await Assert.That(env["KCAP_CODEX_PATH"]).IsEqualTo("/x/codex");
    }

    [Test]
    public async Task Does_not_pin_an_empty_profile() {
        var env = DaemonServiceCommands.EnsureUnitEnv(null, "https://s.example", new Dictionary<string, string>());
        await Assert.That(env.ContainsKey("KCAP_PROFILE")).IsFalse();
    }

    // URL resolution outranks profile resolution (ProfileResolver), so a pinned profile must not sit
    // beside an ambient KCAP_URL — the unit would boot against the wrong server and refuse on the
    // expectation mismatch. The pin is the sole URL authority.
    [Test]
    public async Task Pinning_a_profile_strips_an_ambient_kcap_url() {
        var env = DaemonServiceCommands.EnsureUnitEnv("acme", "https://s.example",
            new Dictionary<string, string> { ["KCAP_URL"] = "https://other.example", ["PATH"] = "/usr/bin" });
        await Assert.That(env.ContainsKey("KCAP_URL")).IsFalse();
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("acme");
        await Assert.That(env["PATH"]).IsEqualTo("/usr/bin");
    }

    // URL-only (no profile) keeps the ambient URL — coherent only off-macOS, where no gate exists.
    [Test]
    public async Task Url_only_install_keeps_the_ambient_kcap_url() {
        var env = DaemonServiceCommands.EnsureUnitEnv(null, "https://s.example",
            new Dictionary<string, string> { ["KCAP_URL"] = "https://s.example" });
        await Assert.That(env["KCAP_URL"]).IsEqualTo("https://s.example");
        await Assert.That(env.ContainsKey("KCAP_PROFILE")).IsFalse();
    }
}

/// <summary>Ensure JSON is snake-cased on the wire and renders through the shared context.</summary>
public class ServiceEnsureJsonTests {
    [Test]
    public async Task Renders_snake_case_fields() {
        var json = ServiceEnsureRender.RenderJson(new ServiceEnsureJson("svc", "not_installed", "install", "installed", Verified: true));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("service_id").GetString()).IsEqualTo("svc");
        await Assert.That(r.GetProperty("action").GetString()).IsEqualTo("install");
        await Assert.That(r.GetProperty("outcome").GetString()).IsEqualTo("installed");
        await Assert.That(r.GetProperty("verified").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Recovery_and_reason_are_nullable() {
        var json = ServiceEnsureRender.RenderJson(new ServiceEnsureJson("svc", "installed", "start", "refused", "takeover", "directive_missing"));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("recovery").GetString()).IsEqualTo("takeover");
        await Assert.That(r.GetProperty("reason").GetString()).IsEqualTo("directive_missing");
    }

    // A refused launchd transaction ran the verified transaction — the JSON must say so, so the
    // flow's copy can distinguish a verified gate refusal from a degraded plain failure.
    [Test]
    public async Task Refused_launchd_transaction_serializes_verified_true() {
        var json = ServiceEnsureRender.RenderJson(new ServiceEnsureJson("svc", "installed", "start", "refused", "takeover", "identity_mismatch", Verified: true));
        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("verified").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Serializes_through_the_shared_context() {
        var json = JsonSerializer.Serialize(new ServiceEnsureJson("svc", "running", "none", "already_enabled"), ServiceJsonContext.Default.ServiceEnsureJson);
        await Assert.That(json).Contains("\"outcome\":\"already_enabled\"");
    }
}

/// <summary>The ensure JSON's state token derives from unit presence: launchd's stopped-but-installed
/// shape reads state NotInstalled (label not loaded) with unitPresent true, and the wire state must
/// not contradict the ladder's own "start" action.</summary>
public class ServiceEnsureStateTokenTests {
    [Test]
    public async Task Stopped_but_installed_unit_reads_installed() {
        var q = new ServiceQuery(LabelProbe.Absent, UnitPresent: true, ServiceState.NotInstalled, null, null);
        await Assert.That(DaemonServiceCommands.ServiceStateToken(q)).IsEqualTo("installed");
    }

    [Test]
    public async Task No_unit_reads_not_installed() {
        var q = new ServiceQuery(LabelProbe.Absent, UnitPresent: false, ServiceState.NotInstalled, null, null);
        await Assert.That(DaemonServiceCommands.ServiceStateToken(q)).IsEqualTo("not_installed");
    }

    [Test]
    public async Task Running_reads_running() {
        var q = new ServiceQuery(LabelProbe.Loaded, UnitPresent: true, ServiceState.Running, null, 42);
        await Assert.That(DaemonServiceCommands.ServiceStateToken(q)).IsEqualTo("running");
    }

    [Test]
    public async Task Unknown_probe_reads_unknown() {
        var q = new ServiceQuery(LabelProbe.Unknown, UnitPresent: true, ServiceState.NotInstalled, null, null);
        await Assert.That(DaemonServiceCommands.ServiceStateToken(q)).IsEqualTo("unknown");
    }
}

/// <summary>Pure mapping of a failure exit to the JSON's recovery/reason fields — the wire contract
/// the flow consumes, testable without a real service manager.</summary>
public class EnsureFailureMapTests {
    [Test]
    [Arguments(VerifyExit.StartGate, StartGateReason.DirectiveMissing, "takeover", "directive_missing")]
    [Arguments(VerifyExit.StartGate, StartGateReason.DirectiveInvalid, "takeover", "directive_invalid")]
    [Arguments(VerifyExit.StartGate, StartGateReason.IdentityMismatch, "takeover", "identity_mismatch")]
    [Arguments(VerifyExit.StartGate, StartGateReason.ForeignBinary, "takeover", "foreign_binary")]
    [Arguments(VerifyExit.StartGate, StartGateReason.PackageInconsistent, "reinstall", "package_inconsistent")]
    [Arguments(VerifyExit.StartGate, StartGateReason.EvidenceUnreadable, "attention", "evidence_unreadable")]
    internal async Task StartGate_routes_to_its_recovery_surface(int exit, StartGateReason reason, string recovery, string token) {
        var (r, reasonToken) = EnsureFailureMap.Map(exit, reason, verified: true);
        await Assert.That(r).IsEqualTo(recovery);
        await Assert.That(reasonToken).IsEqualTo(token);
    }

    [Test]
    public async Task StartGate_without_a_reason_fails_closed_to_attention() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.StartGate, null, verified: true);
        await Assert.That(r).IsEqualTo("attention");
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Drift_is_the_gates_toctou_refusal_attention_with_its_token() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.StartGateDrift, null, verified: true);
        await Assert.That(r).IsEqualTo("attention");
        await Assert.That(reason).IsEqualTo("verify_start_gate_drift");
    }

    [Test]
    public async Task Verified_non_gate_exit_carries_its_verify_token() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.Viability, null, verified: true);
        await Assert.That(r).IsNull();
        await Assert.That(reason).IsEqualTo("verify_viability");
    }

    // A gated-install viability abort with the engine's coded reason routes to reinstall — the same
    // package_inconsistent token the start gate's own table maps — never the generic verify token.
    [Test]
    public async Task Viability_with_a_package_inconsistent_reason_routes_to_reinstall() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.Viability, null, verified: true, viabilityReason: "package_inconsistent");
        await Assert.That(r).IsEqualTo("reinstall");
        await Assert.That(reason).IsEqualTo("package_inconsistent");
    }

    // An attributed readiness-timeout boot refusal carries its marker token through ForBootRefusal:
    // a consent_seed_unwritable refusal routes to storage, never the generic verify_readiness_timeout.
    [Test]
    public async Task Readiness_timeout_with_attributed_boot_refusal_routes_to_storage() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.ReadinessTimeout, null, verified: true, bootRefusalToken: "consent_seed_unwritable");
        await Assert.That(r).IsEqualTo("storage");
        await Assert.That(reason).IsEqualTo("consent_seed_unwritable");
    }

    [Test]
    public async Task Readiness_timeout_with_a_server_expectation_refusal_routes_to_takeover() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.ReadinessTimeout, null, verified: true, bootRefusalToken: "server_expectation_mismatch");
        await Assert.That(r).IsEqualTo("takeover");
        await Assert.That(reason).IsEqualTo("server_expectation_mismatch");
    }

    [Test]
    public async Task Readiness_timeout_without_attribution_keeps_the_generic_token() {
        var (r, reason) = EnsureFailureMap.Map(VerifyExit.ReadinessTimeout, null, verified: true);
        await Assert.That(r).IsNull();
        await Assert.That(reason).IsEqualTo("verify_readiness_timeout");
    }

    [Test]
    public async Task Plain_failure_never_wears_the_verify_prefix() {
        var (r, reason) = EnsureFailureMap.Map(1, null, verified: false);
        await Assert.That(r).IsNull();
        await Assert.That(reason).IsEqualTo("plain_failure");
    }
}

/// <summary>
/// The collapse of an ensure result into the first-run flow's closed vocabulary.
///
/// <para><b>What these pin is the one failure the wire cannot catch.</b> An unknown token is refused by
/// the server and the report strands loudly; a token that is valid but wrong for the row is accepted, and
/// shows up only as the wrong sentence and the wrong button — a retry offered on a broken install, or
/// withheld from a busy one.</para>
/// </summary>
public class EnsureFlowMapTests {
    static ServiceEnsureJson Ladder(string outcome, string? reason = null, bool verified = false) =>
        new("kcap", "not_installed", "none", outcome, null, reason, verified);

    [Test]
    public async Task Already_enabled_says_so_rather_than_claiming_a_change() {
        var r = EnsureFlowMap.Map(Ladder("already_enabled"));
        await Assert.That(r.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.AlreadyEnabled);
        await Assert.That(r.Reason).IsNull();
    }

    [Test]
    [Arguments("installed")]
    [Arguments("started")]
    public async Task Install_and_start_collapse_but_verified_does_not(string outcome) {
        await Assert.That(EnsureFlowMap.Map(Ladder(outcome, verified: true)).Outcome)
            .IsEqualTo(FirstRunMachineActionOutcomes.Enabled);

        await Assert.That(EnsureFlowMap.Map(Ladder(outcome, verified: false)).Outcome)
            .IsEqualTo(FirstRunMachineActionOutcomes.EnabledUnverified);
    }

    // Both sources mean the same thing to the screen: nothing was mutated and the machine's state moved
    // under us, so a retry re-decides. `txn_active` is a held lock; `verify_contended` is either that or
    // a fresh install refusing a unit that appeared after the state was read.
    [Test]
    [Arguments("txn_active")]
    [Arguments("verify_contended")]
    public async Task Nothing_mutated_and_the_state_moved_is_retryable(string reason) {
        var r = EnsureFlowMap.Map(Ladder("attention", reason));
        await Assert.That(r.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(r.Reason).IsEqualTo(FirstRunMachineActionReasons.ServiceBusy);
    }

    // Viability is proven before anything destructive, so an unusable pinned URL is a misconfiguration
    // rather than a transaction that failed — and no retry can help it.
    [Test]
    [Arguments("no_profile_configured")]
    [Arguments("no_server_configured")]
    [Arguments("daemon_not_found")]
    [Arguments("verify_viability")]
    public async Task Nothing_to_run_it_for_offers_no_retry(string reason) {
        var r = EnsureFlowMap.Map(Ladder("refused", reason));
        await Assert.That(r.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(r.Reason).IsEqualTo(FirstRunMachineActionReasons.NotConfigured);
    }

    [Test]
    [Arguments("status_unknown")]
    [Arguments("orphan_label")]
    [Arguments("stale_marker")]
    [Arguments("running_unconfirmed")]
    public async Task A_machine_the_ladder_will_not_touch_needs_attention(string reason) {
        var r = EnsureFlowMap.Map(Ladder("attention", reason));
        await Assert.That(r.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(r.Reason).IsEqualTo(FirstRunMachineActionReasons.NeedsAttention);
    }

    /// <summary>
    /// <c>package_inconsistent</c> has two sources — a viability abort's digest mismatch and the start
    /// gate's binary hash check — and both mean a broken install. <b>Keyed on the reason, never on the
    /// exit</b>: keying on the viability exit would send the start-gate source to the tail and put a
    /// retry button on an install that cannot be retried into working.
    /// </summary>
    [Test]
    public async Task Package_inconsistent_is_keyed_on_the_reason_not_the_exit() {
        var viability = EnsureFailureMap.Map(VerifyExit.Viability, null, verified: true, viabilityReason: "package_inconsistent");
        var startGate = EnsureFailureMap.Map(VerifyExit.StartGate, StartGateReason.PackageInconsistent, verified: true);

        await Assert.That(startGate.Reason).IsEqualTo(viability.Reason);

        foreach (var mapped in (FirstRunMachineActionResult[])[
                     EnsureFlowMap.Map(Ladder("refused", viability.Reason)),
                     EnsureFlowMap.Map(Ladder("refused", startGate.Reason))]) {
            await Assert.That(mapped.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
            await Assert.That(mapped.Reason).IsEqualTo(FirstRunMachineActionReasons.NeedsAttention);
        }
    }

    /// <summary>
    /// Every coded verify exit reaches the row the governing rule puts it in — <c>refused</c> where
    /// nothing was mutated, <c>failed</c> where a transaction ran and did not land.
    ///
    /// <para><b>Driven off <see cref="VerifyExit"/>'s own members</b>, so an exit added to the ladder
    /// fails here with nowhere to go rather than falling silently into the tail.</para>
    /// </summary>
    [Test]
    public async Task Every_coded_exit_lands_on_the_row_the_rule_puts_it_in() {
        var expected = new Dictionary<int, string?> {
            [VerifyExit.Contended]           = FirstRunMachineActionReasons.ServiceBusy,
            [VerifyExit.Viability]           = FirstRunMachineActionReasons.NotConfigured,
            [VerifyExit.BootoutUnknown]      = null,
            [VerifyExit.StopUnconfirmed]     = null,
            [VerifyExit.ReadinessTimeout]    = null,
            [VerifyExit.HelloValidation]     = null,
            [VerifyExit.RollbackBudget]      = null,
            [VerifyExit.RestoreVerification] = null,
            [VerifyExit.StartGate]           = FirstRunMachineActionReasons.NeedsAttention,
            [VerifyExit.StartGateDrift]      = null,
        };

        var codes = typeof(VerifyExit)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue()!)
            .Where(code => code != VerifyExit.Ok)
            .ToList();

        // The table has to cover the ladder, not the other way round: an unlisted exit is one nobody
        // decided a row for.
        await Assert.That(codes.Except(expected.Keys)).IsEmpty();

        foreach (var code in codes) {
            // A start-gate exit always carries a gate reason in production, so driving it without one
            // would exercise a branch the engine never takes.
            var gate = code == VerifyExit.StartGate ? StartGateReason.IdentityMismatch : (StartGateReason?)null;

            var (_, reason) = EnsureFailureMap.Map(code, gate, verified: true);
            var mapped      = EnsureFlowMap.Map(Ladder("refused", reason));

            // `null` in the table means the tail: a transaction ran, so `failed` and no reason.
            if (expected[code] is { } wanted) {
                await Assert.That(mapped.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
                await Assert.That(mapped.Reason).IsEqualTo(wanted);
            } else {
                await Assert.That(mapped.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
                await Assert.That(mapped.Reason).IsNull();
            }
        }
    }

    /// <summary>
    /// Every start-gate reason refuses rather than fails, and none of them offers a retry: the gate
    /// declines in its first phase, before the marker write, so nothing was mutated — and each reason
    /// means something else owns or has invalidated the install.
    ///
    /// <para>Driven off <see cref="StartGateReason"/>'s own members, so a reason added to the gate has
    /// to be given a row rather than falling into the tail with a retry button on it.</para>
    /// </summary>
    [Test]
    public async Task Every_start_gate_reason_needs_attention_and_offers_no_retry() {
        foreach (var reason in Enum.GetValues<StartGateReason>()) {
            var (_, token) = EnsureFailureMap.Map(VerifyExit.StartGate, reason, verified: true);
            var mapped     = EnsureFlowMap.Map(Ladder("refused", token));

            await Assert.That(mapped.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
            await Assert.That(mapped.Reason).IsEqualTo(FirstRunMachineActionReasons.NeedsAttention);
        }
    }

    // Drift is the other half of the gate and belongs on the other side of the rule: it re-checks after
    // the marker and a bootout, so a transaction genuinely ran and was rolled back.
    [Test]
    public async Task Gate_drift_failed_rather_than_refused_because_something_ran() {
        var (_, token) = EnsureFailureMap.Map(VerifyExit.StartGateDrift, null, verified: true);
        var mapped     = EnsureFlowMap.Map(Ladder("refused", token));

        await Assert.That(mapped.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(mapped.Reason).IsNull();
    }

    // A refusal whose reason matched no row belongs to the tail, and so does one carrying none at all.
    [Test]
    [Arguments("plain_failure")]
    [Arguments(null)]
    public async Task An_unmatched_refusal_falls_to_the_tail(string? reason) {
        var r = EnsureFlowMap.Map(Ladder("refused", reason));
        await Assert.That(r.Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(r.Reason).IsNull();
    }

    // Whatever the map produces has to be sayable on the wire: the server refuses an unrecognised token
    // outright, after which the CLI retries for ever and the screen waits on an answer it already has.
    [Test]
    public async Task Nothing_it_produces_is_off_the_closed_sets() {
        string?[] reasons = [
            "txn_active", "verify_contended", "no_profile_configured", "no_server_configured",
            "daemon_not_found", "verify_viability", "status_unknown", "orphan_label", "stale_marker",
            "running_unconfirmed", "package_inconsistent", "plain_failure", "verify_readiness_timeout",
            "server_expectation_mismatch", null
        ];

        foreach (var outcome in (string[])["already_enabled", "installed", "started", "attention", "refused"])
        foreach (var reason in reasons) {
            var r = EnsureFlowMap.Map(Ladder(outcome, reason));

            await Assert.That(FirstRunMachineActionOutcomes.IsKnown(r.Outcome)).IsTrue();
            await Assert.That(r.Reason is null || FirstRunMachineActionReasons.IsKnown(r.Reason)).IsTrue();
        }
    }
}
