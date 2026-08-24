using System.Text.Json;
using Capacitor.Cli.Commands;
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
