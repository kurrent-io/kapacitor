using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Startup phase, reconciliation, and the §4.2 startup matrix (spec). Every
/// clock-dependent wait goes through FakeTimeProvider (never Task.Delay-based ordering);
/// settling between an event push and its effect is driven by WaitUntilAsync polling on the
/// fakes' call counters (PauseControllerTests/ConsentServiceTests idiom).
///
/// Task 10: every mutating branch now routes through a fake lane (FakeMutationLane)
/// instead of calling IKcapCli's mutation methods directly — FakeKcapCli's
/// StartVerified/InstallVerified/DetachedStart call counts are kept as a belt-and-braces
/// regression guard (they must stay 0 everywhere) alongside the new Lane.Requests assertions.
public class DaemonLifecycleControllerTests {
    static readonly TimeSpan TxnActiveRequeryDelay = DaemonLifecycleController.TxnActiveRequeryDelay;

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static ServiceSnapshot Snap(
            bool unitPresent = false, string state = "not_installed", string? installBinaryPath = "/opt/kcap/kcapd",
            string? binaryPath = null, int? jobPid = null, int? daemonPid = null, bool txnMarker = false,
            bool txnActive = false) =>
        new("default", unitPresent, state, binaryPath, installBinaryPath, jobPid, daemonPid, txnMarker, txnActive);

    // ---- startup matrix rows (§4.2) ----

    [Test]
    public async Task Row1_job_running_is_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the matrix status query");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row2_loaded_inactive_plist_present_daemonPid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service start --verify request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // Never IKcapCli directly — the lane is the ONLY caller of these now.
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row2b_loaded_inactive_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row3_orphan_label_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the orphan-label attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row4_no_label_plist_present_daemonPid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service start --verify request (bootstrap)");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row4b_no_label_plist_present_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed", daemonPid: 777));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_preconditions_pass_installs_verified_without_replace() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service install --verify request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install); // never Replace on the silent-install row
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // An unrecognized wire state must never fall through to the NotInstalled
    // (auto-install/start) branch — positive evidence only.
    [Test]
    public async Task Unrecognized_state_is_status_only_no_auto_install_or_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest unrecognized-state line");
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_no_profile_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.ProfileName = null;
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-profile line");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_path_unknown_is_status_only_silent_install_suppressed() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        var beforePhaseClosed = h.Controller.PhaseClosed.IsCompleted;
        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest PATH-unknown line");
        await h.Controller.PhaseClosed;
        await Assert.That(beforePhaseClosed).IsFalse();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row4_starts_verified_even_when_terminal_PATH_is_unknown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request despite unknown PATH");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    [Test]
    public async Task Row5_nothing_installBinaryPath_null_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(installBinaryPath: null));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-install-binary line");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- Task 15 decision-2 carve-out: autoActionsPermanentlyClosed ----

    [Test]
    public async Task AutoActionsClosed_terminal_unreachable_admits_no_startup_matrix_but_still_closes_the_phase() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // would otherwise auto-install
        h.Start();

        h.PushUnreachable();

        await h.Controller.PhaseClosed; // must complete even though the matrix never runs
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(0); // RunStartupBranchAsync never admitted
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // Closed mode adds no separate arm — a second unreachable stays just as inert as the open-graph one.
    [Test]
    public async Task AutoActionsClosed_second_unreachable_stays_inert() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();
        await h.Controller.PhaseClosed;
        h.PushUnreachable();

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(0);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // StartActionAsync is a different code path from RunStartupBranchAsync — closing the latter must not touch it.
    [Test]
    public async Task AutoActionsClosed_user_clicked_StartAction_still_routes_through_the_lane() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — DetachedStart row
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.DetachedStart);
    }

    // ---- txn_active defers the startup matrix (spec §6: wait and re-query, never mutate into a held flock) ----

    [Test]
    public async Task TxnActive_defers_the_matrix_until_the_one_requery_clears_it() {
        await using var h = new Harness();
        var call = 0;
        h.Cli.StatusBehavior = _ => {
            call++;
            return Task.FromResult<ServiceSnapshot?>(call == 1 ? Snap(txnActive: true) : Snap(txnActive: false));
        };
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the initial matrix query");
        await Assert.That(h.Lane.Requests).IsEmpty(); // deferred — not mutated into the held flock

        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the one bounded requery");
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the matrix proceeding once the flock cleared");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install);
    }

    [Test]
    public async Task TxnActive_still_active_after_the_one_requery_takes_no_action_this_run() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnActive: true));
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the initial matrix query");
        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the one bounded requery");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- a racing (meaningfully different) event forces one re-evaluation instead of silence ----

    [Test]
    public async Task Racing_connected_during_the_startup_query_forces_a_fresh_reevaluation_in_attached_mode() {
        await using var h = new Harness();
        var first = new TaskCompletionSource<ServiceSnapshot?>();
        var call = 0;
        h.Cli.StatusBehavior = _ => {
            call++;
            return call == 1 ? first.Task : Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 200));
        };
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the first (pending) status query");

        h.PushConnected(); // races the in-flight query with a MEANINGFULLY different outcome
        first.SetResult(Snap(state: "running", jobPid: 1, daemonPid: 1)); // release the now-stale first query

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the forced re-evaluation against fresh state");

        // The re-evaluation must reconcile in ATTACHED mode (we're now actually Connected) — the
        // ownership-mismatch check only fires while attached, so this proves the race no longer
        // strands the run's only reconciliation pass in permanently-unattached mode.
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the attached-mode ownership-mismatch attention");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("100");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("200");
    }

    // ---- startup phase closes on the FIRST terminal outcome ----

    [Test]
    public async Task Connected_first_then_unreachable_is_inert() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;

        h.PushUnreachable();
        await Task.Delay(50); // a negative: give a would-be matrix run every chance to fire

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Incompatible_first_runs_reconciliation_then_unreachable_is_inert() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnMarker: true, txnActive: false));
        h.Start();

        h.PushUnreachable(reason: "daemon_incompatible");
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "reconciliation runs on the incompatible path too");
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention it found");

        h.PushUnreachable(); // daemon_unreachable, a later terminal outcome — inert (arm already claimed)
        await Task.Delay(50);

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- once-per-run arm ----

    [Test]
    public async Task Two_daemon_unreachable_in_a_row_consult_status_exactly_once() {
        await using var h = new Harness();
        var gate = new TaskCompletionSource<ServiceSnapshot?>();
        h.Cli.StatusBehavior = _ => gate.Task; // hangs until released below

        h.Start();
        h.PushUnreachable(); // arm claimed synchronously before this await
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the first (pending) status query");

        h.PushUnreachable(); // arm already claimed — must not issue a second query
        await Task.Delay(50);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);

        gate.SetResult(Snap(state: "running", jobPid: 1, daemonPid: 1)); // release — a no-op row either way
        await h.Controller.PhaseClosed;
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
    }

    // ---- reconciliation on immediate Connected ----

    [Test]
    public async Task Reconciliation_on_connected_nonOwning_loaded_label_is_attention() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 200));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the ownership-mismatch attention");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("100");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("200");
    }

    [Test]
    public async Task Reconciliation_on_connected_stale_txn_marker_is_attention() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnMarker: true, txnActive: false));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
    }

    [Test]
    public async Task Reconciliation_on_connected_txnActive_schedules_one_requery_no_attention_yet() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnActive: true));
        h.Start();

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();

        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the single scheduled requery");

        // No further scheduling — a second advance must not trigger a third query.
        h.Clock.Advance(TxnActiveRequeryDelay);
        await Task.Delay(50);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(2);
    }

    // ---- Task 10: routing through the lane ----

    [Test]
    public async Task Auto_start_routes_through_the_lane_with_the_pinned_identity() {
        await using var h = new Harness();
        h.Client.DaemonName = "daemon-xyz";
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request");
        var request = h.Lane.Requests[0];
        await Assert.That(request.Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(request.Profile).IsEqualTo("default");
        await Assert.That(request.CanonicalServer).IsEqualTo(h.CanonicalServer);
        await Assert.That(request.DaemonName).IsEqualTo("daemon-xyz");
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0); // never IKcapCli directly
    }

    [Test]
    public async Task No_canonical_server_refuses_without_ever_calling_the_lane() {
        await using var h = new Harness(canonicalServer: null);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the no-server status line");
        await Assert.That(h.Lane.Requests).IsEmpty(); // the guard runs BEFORE the lane is ever touched
        await Assert.That(h.Surface.StatusMessages[0]).Contains("no_server_configured");
    }

    // Single-presentation rule: an outcome the lane already routes to the outcome channel
    // (AttentionSkew, here) must never ALSO be raised directly by the controller.
    [Test]
    public async Task AttentionSkew_outcome_does_not_raise_the_controllers_direct_attention_surface() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.AttentionSkew("ownership_mismatch"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Attention/Status every chance to appear
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        // Blocker 1: the reattach kick is unconditional after any lane mutation call — a mutation
        // attempt may have restarted the daemon even though this outcome isn't itself a success.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    // An UnconfirmedNoAttach outcome (the lane's TimedOut classification, spec §3.6) supersedes
    // the controller's former confirm-window/timeout handling entirely — no local surface call at
    // all, channel-only, same as every other non-success outcome.
    [Test]
    public async Task UnconfirmedNoAttach_outcome_produces_no_controller_surface_call_but_still_kicks_reattach() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.UnconfirmedNoAttach());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // Blocker 1: any lane mutation attempt may have restarted the daemon, so the kick is
        // unconditional — not gated on this outcome being a success.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    // Round-1 review C-2: a Refused outcome that reached the LANE (as opposed to the guard
    // refusing before ever touching it — see No_canonical_server_refuses_without_ever_calling_the_lane
    // above) was already enqueued onto the outcome channel by the lane's own Deliver — the
    // controller must make NO surface call of its own, or the composition-root consumer's
    // presentation doubles up.
    [Test]
    public async Task Refused_outcome_from_the_lane_produces_no_controller_surface_call() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Status/Attention every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // ---- UX confirmation ----

    [Test]
    public async Task Successful_mutation_kicks_restart_no_status_message() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request to begin");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);

        release.SetResult(new MutationOutcome.Succeeded());

        await WaitUntilAsync(() => h.Client.RestartCount >= 1, what: "the post-mutation reattach kick");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing message every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task SucceededAfterTimeout_also_kicks_restart_no_status_message() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.SucceededAfterTimeout());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Client.RestartCount >= 1, what: "the post-mutation reattach kick");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }

    // ---- coded failure ----

    // Round-1 review C-2: a Failed outcome from the lane is presented ONLY by the composition-root
    // consumer now (see AppMutationLaneWiringTests.PresentOutcomeAsync's Attention/Reinstall/
    // Takeover coverage for the actual message content) — the controller itself makes no surface
    // call, but the once-per-run arm still holds (no retry on a second daemon_unreachable).
    [Test]
    public async Task Failed_outcome_from_the_lane_produces_no_controller_surface_call_but_counts_the_run_once() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(24, "gave_up_waiting", RecoverySurface.Attention));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Status/Attention every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // Blocker 1: a Failed outcome may still mean the daemon got restarted mid-mutation — the
        // kick is unconditional, not gated on Succeeded/SucceededAfterTimeout.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);

        h.PushUnreachable(); // once-per-run: no retry
        await Task.Delay(50);
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Client.RestartCount).IsEqualTo(1); // no second lane call — no second kick
    }

    // ---- version caching ----

    [Test]
    public async Task Start_caches_the_cli_version_once() {
        await using var h = new Harness();
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>("9.9.9");
        h.Start();

        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the one-shot version probe");
        await Assert.That(h.Controller.CliVersion).IsEqualTo("9.9.9");
    }

    // ---- quiescence ----

    [Test]
    public async Task QuiescedAsync_waits_for_an_in_flight_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the in-flight mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        release.SetResult(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        await quiesced;
    }

    // ---- disposal ----

    [Test]
    public async Task DisposeAsync_is_idempotent() {
        var h = new Harness();
        h.Start();

        await h.Controller.DisposeAsync();
        await h.Controller.DisposeAsync(); // must not throw ObjectDisposedException
    }

    // ---- §4.4 Start action (light coverage — Task 21 wires the trigger) ----

    [Test]
    public async Task StartAction_job_running_kicks_reattach_without_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 1, daemonPid: 1));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_nothing_at_all_falls_back_to_detached_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.DetachedStart);
        // Task 10: DetachedStart from Start now shares the SAME success handling as every other
        // verb (a strict improvement — it used to be a bare, result-discarding CLI call).
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    [Test]
    public async Task StartAction_loaded_plist_present_pid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_loaded_pid_nonNull_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_orphan_label_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_no_label_plist_present_pid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request (bootstrap)");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_no_label_plist_present_pid_nonNull_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed", daemonPid: 777));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_repair_accept_calls_replace_same_helper_as_takeover() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the repair request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Replace);

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_repair_decline_does_not_mutate() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_status_unknown_takes_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    // Counterpart for the Start action's own state-classification switch.
    [Test]
    public async Task StartAction_unrecognized_state_is_status_only_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_generic_exception_is_status_surfaced_not_thrown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => throw new InvalidOperationException("boom");
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None); // must not throw

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
    }

    // Holds the gate open with a pending startup-triggered install (TCS-scripted), then invokes
    // StartActionAsync concurrently: it must block on the gate rather than act on stale evidence,
    // and once the gate clears it re-queries (a SECOND ServiceStatusAsync call) before acting —
    // never reusing whatever it might have read before the wait.
    [Test]
    public async Task StartAction_racing_auto_install_awaits_the_gate_then_reQueries_fresh_evidence() {
        await using var h = new Harness();
        var install = new TaskCompletionSource<MutationOutcome>();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — the install row
        h.Lane.Behavior = (_, _) => install.Task;
        h.Start();

        h.PushUnreachable(); // claims the arm, starts the startup matrix, blocks on the install call
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install to begin (holding the gate)");
        var statusCallsBeforeStart = h.Cli.StatusCallCount;

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await Task.Delay(50); // give a wrongly-unblocked Start every chance to act early
        await Assert.That(startTask.IsCompleted).IsFalse();
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCallsBeforeStart); // no re-query yet — still blocked

        install.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;

        await Assert.That(h.Cli.StatusCallCount).IsGreaterThan(statusCallsBeforeStart); // a fresh query after the gate cleared
    }

    [Test]
    public async Task QuiescedAsync_waits_for_a_startAction_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the Start-triggered mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        release.SetResult(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        await quiesced;
        await startTask;
    }

    // ---- §4.3 skew → restart/takeover ----

    [Test]
    public async Task Skew_connected_version_match_is_noop() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("1.0.0"); // matches FakeKcapCli's default VersionBehavior
        h.PushConnected();

        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing prompt every chance to appear
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Skew_connected_mismatch_same_canonical_binary_prompts_restart_update() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("2.0.0");
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt");
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRestartUpdate);
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("2.0.0");
        await Assert.That(h.Surface.Prompts[0].CliVersion).IsEqualTo("1.0.0");
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
    }

    [Test]
    public async Task Skew_connected_mismatch_different_binary_prompts_takeover() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/usr/local/bin/kcapd-old"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("2.0.0");
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt");
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
    }

    [Test]
    public async Task Skew_incompatible_version_mismatch_prompts() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the incompatible skew prompt");
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("0.9");
    }

    [Test]
    public async Task Skew_version_flip_same_run_prompts_only_once() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the first skew prompt");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.95"); // a genuinely new pair
        await Task.Delay(50); // give a second (wrongly-stacked) prompt every chance to appear

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Skew_daemon_unreachable_never_prompts() {
        await using var h = new Harness();
        // state:"running" is a no-mutation matrix row (Row1) — a mutating row would block this
        // test's PhaseClosed wait on the lane, which nothing here resolves.
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 1, daemonPid: 1));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(daemonVersion: "9.9.9"); // reason defaults to daemon_unreachable

        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Skew_cli_version_null_disables_detection() {
        await using var h = new Harness();
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>(null);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version probe attempt");
        await Assert.That(h.Controller.CliVersion).IsNull();

        h.PushSnapshot("2.0.0");
        h.PushConnected();

        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Skew_prompt_discloses_degraded_path_when_terminal_path_unknown() {
        await using var h = new Harness();
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt");
        await Assert.That(h.Surface.Prompts[0].PathDegraded).IsTrue();
    }

    [Test]
    public async Task Skew_accept_calls_replace_and_nothing_else() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the takeover request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Replace);
    }

    [Test]
    public async Task Skew_decline_persists_pair_same_pair_no_prompt_new_pair_prompts() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the first skew prompt");

        var afterDecline = await h.Store.LoadAsync();
        await Assert.That(afterDecline.DeclinedTakeoverPairs).IsNotNull();
        await Assert.That(afterDecline.DeclinedTakeoverPairs!).Contains("0.9|1.0.0");

        // A fresh controller sharing the same store, offered the SAME pair, must not prompt.
        var surface2 = new FakeLifecycleSurface();
        var client2  = new FakeDaemonClientService();
        var cli2     = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed")) };
        await using var controller2 = new DaemonLifecycleController(
            client2, cli2, new FakeLoginShellProbe(), h.Store, surface2, () => Task.FromResult<string?>("default"), h.Time,
            h.CanonicalServer, new FakeMutationLane().RunAsync);
        controller2.Start();
        await WaitUntilAsync(() => cli2.VersionCallCount == 1, what: "controller2's version cache");

        client2.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null, "0.9"));
        await Task.Delay(50);
        await Assert.That(surface2.Prompts).IsEmpty();

        // A genuinely new pair (either version changed) offers again.
        client2.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null, "0.95"));
        await WaitUntilAsync(() => surface2.Prompts.Count == 1, what: "the new-pair prompt");
    }

    [Test]
    public async Task Skew_stale_consent_between_show_and_accept_aborts_no_mutation() {
        await using var h = new Harness();
        var confirmTcs = new TaskCompletionSource<bool>();
        h.Surface.ConfirmBehavior = (_, _) => confirmTcs.Task;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt shown");

        h.PushUnreachable(); // a later, unrelated attach transition — moves the generation
        confirmTcs.SetResult(true); // the user accepts what is now a stale offer

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the stale-consent abort status");
        await Assert.That(h.Lane.Requests).IsEmpty();

        // A stale accept was never a real decline — the claim-before-show pair is retracted...
        var afterAbort = await h.Store.LoadAsync();
        await Assert.That((afterAbort.DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0")).IsFalse();

        // ...and the once-per-run flag is cleared so a fresh trigger re-offers (spec §6).
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 2, what: "the re-offered prompt");
    }

    // Finding 7: a terminal can replace the plist/unit while the dialog is open WITHOUT producing
    // an attach event — the generation token alone is blind to this. Acceptance must re-query
    // fresh status and re-classify before mutating; a classification flip aborts exactly like the
    // generation-based stale-consent path above.
    [Test]
    public async Task Skew_accept_aborts_when_fresh_status_reclassifies_without_a_generation_bump() {
        await using var h = new Harness();
        var confirmTcs = new TaskCompletionSource<bool>();
        h.Surface.ConfirmBehavior = (_, _) => confirmTcs.Task;
        var sameBinary      = Snap(unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd");
        var differentBinary = Snap(unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/usr/local/bin/kcapd-old");
        var noMutationRow   = Snap(state: "running", jobPid: 1, daemonPid: 1);
        var next = noMutationRow; // swapped explicitly at each step below — never inferred from call count
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(next);
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        // Claim the once-per-run arm on a harmless no-mutation row first — the daemon_incompatible
        // push below must NOT be the run's first terminal outcome, or it would ALSO fire a
        // concurrent RunReconciliationAsync status query racing RunSkewCheckAsync's own two
        // (dialog-build + in-gate revalidation), making "which call sees which snapshot"
        // nondeterministic instead of exercising the intended sequence.
        h.PushUnreachable();
        await h.Controller.PhaseClosed;

        next = sameBinary; // the dialog-build query below
        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt shown");
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRestartUpdate);

        next = differentBinary; // a terminal silently replaced the plist mid-dialog — no attach event
        confirmTcs.SetResult(true); // accept — generation is unchanged

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the reclassification abort status");
        await Assert.That(h.Lane.Requests).IsEmpty();

        // Never a real decline — the claim is retracted and the run flag cleared, same as the
        // generation-based stale-consent path.
        var afterAbort = await h.Store.LoadAsync();
        await Assert.That((afterAbort.DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0")).IsFalse();

        next = sameBinary;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 2, what: "the re-offered prompt");
    }

    // Unchanged evidence between show and accept proceeds — the counterpart to the reclassification
    // abort above. Distinct from Skew_accept_calls_replace_and_nothing_else: this one asserts the
    // revalidation query itself ran (a StatusCallCount delta of 2) rather than just the eventual
    // lane request.
    [Test]
    public async Task Skew_accept_with_unchanged_status_revalidates_then_proceeds() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var installedSnap = Snap(unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd");
        var noMutationRow = Snap(state: "running", jobPid: 1, daemonPid: 1);
        var next = noMutationRow;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(next);
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        // Claim the arm on a harmless no-mutation row first — see the comment on
        // Skew_accept_aborts_when_fresh_status_reclassifies_without_a_generation_bump above for why
        // daemon_incompatible must not be the run's first terminal outcome here.
        h.PushUnreachable();
        await h.Controller.PhaseClosed;
        var statusCallsBeforeSkew = h.Cli.StatusCallCount;

        next = installedSnap;
        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the takeover request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(h.Cli.StatusCallCount - statusCallsBeforeSkew).IsEqualTo(2); // the dialog query + the in-gate revalidation
    }

    [Test]
    public async Task Skew_accept_coded_failure_retracts_claim_and_clears_run_flag() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(21, "verify_viability_nope", RecoverySurface.Attention));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        // Lane.Behavior resolves immediately (Task.FromResult), and the retract is fully awaited
        // BEFORE the mutation call (see the comment on the retract-before-mutation ordering
        // above) — by the time the lane records the request, the retract has already landed.
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the takeover request attempt");

        var afterFailure = await h.Store.LoadAsync();
        await Assert.That((afterFailure.DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0")).IsFalse();
        await Assert.That(h.Surface.StatusMessages).IsEmpty(); // channel-only now (round-1 review C-2)

        // The run flag cleared too — a fresh trigger (a different pair, since this run's CLI
        // version hasn't changed) still gets an offer.
        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.95");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 2, what: "the re-offered prompt after the coded failure");
    }

    [Test]
    public async Task Skew_claim_persisted_before_confirm_resolves_survives_a_crash_at_dialog() {
        await using var h = new Harness();
        var confirmTcs = new TaskCompletionSource<bool>();
        h.Surface.ConfirmBehavior = (_, _) => confirmTcs.Task;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the dialog shown");

        // The dialog is "open" (ConfirmAsync hasn't resolved) — simulate a crash right here: the
        // claim must already be on disk, not waiting on the user's answer.
        var midDialog = await h.Store.LoadAsync();
        await Assert.That(midDialog.DeclinedTakeoverPairs).IsNotNull();
        await Assert.That(midDialog.DeclinedTakeoverPairs!).Contains("0.9|1.0.0");

        confirmTcs.SetResult(false); // let it resolve so disposal doesn't wait on it
    }

    [Test]
    public async Task Skew_accept_retracts_the_claim_after_a_successful_takeover() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the takeover request");

        await WaitUntilAsync(
            () => !(h.Store.LoadAsync().GetAwaiter().GetResult().DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0"),
            what: "the claim retracted on acceptance");
    }

    [Test]
    public async Task Skew_accept_retracts_the_claim_even_when_the_mutation_throws() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        // Simulates a shutdown mid-spawn or a lane-lifetime cancellation — RunLaneMutationAsync
        // does not catch around the lane call, so this propagates out of RunSkewCheckAsync's
        // mutation step entirely.
        h.Lane.Behavior = (_, _) => Task.FromException<MutationOutcome>(new OperationCanceledException("shutdown mid-install"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the takeover request attempt");

        await WaitUntilAsync(
            () => !(h.Store.LoadAsync().GetAwaiter().GetResult().DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0"),
            what: "the claim retracted despite the mutation throwing");

        // A fresh controller sharing the same store re-offers the same pair — accepted-but-failed
        // must never read back as "the user declined".
        var surface2 = new FakeLifecycleSurface();
        var client2  = new FakeDaemonClientService();
        var cli2     = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed")) };
        await using var controller2 = new DaemonLifecycleController(
            client2, cli2, new FakeLoginShellProbe(), h.Store, surface2, () => Task.FromResult<string?>("default"), h.Time,
            h.CanonicalServer, new FakeMutationLane().RunAsync);
        controller2.Start();
        await WaitUntilAsync(() => cli2.VersionCallCount == 1, what: "controller2's version cache");

        client2.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null, "0.9"));
        await WaitUntilAsync(() => surface2.Prompts.Count == 1, what: "the re-offered prompt");
    }

    [Test]
    public async Task Skew_connected_before_version_probe_resolves_still_prompts_once_it_does() {
        await using var h = new Harness();
        var versionTcs = new TaskCompletionSource<string?>();
        h.Cli.VersionBehavior = _ => versionTcs.Task;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd"));
        h.Start();

        h.PushSnapshot("2.0.0");
        h.PushConnected(); // the attach cycle wins the race — arrives before the version probe resolves

        await Task.Delay(50); // give a wrongly-early (or wrongly-dropped) prompt every chance to appear
        await Assert.That(h.Surface.Prompts).IsEmpty();

        versionTcs.SetResult("1.0.0"); // the probe finally lands

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt once the version probe resolves");
    }

    [Test]
    public async Task Skew_classification_treats_empty_binary_path_as_takeover_without_throwing() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: ""));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt (no throw on empty path)");
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
    }

    [Test]
    public async Task Skew_classification_resolves_symlinks_to_the_same_canonical_target() {
        await using var h = new Harness();
        var dir  = Directory.CreateTempSubdirectory("kcap-skew-symlink-").FullName;
        var real = Path.Combine(dir, "kcapd-real");
        File.WriteAllText(real, "binary");
        var link = Path.Combine(dir, "kcapd-link");
        File.CreateSymbolicLink(link, real);

        try {
            h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
                unitPresent: true, state: "installed", installBinaryPath: real, binaryPath: link));
            h.Start();
            await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

            h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

            await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt");
            await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRestartUpdate);
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    [Test]
    public async Task Skew_missing_install_binary_path_precondition_fails_status_only_no_prompt() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", installBinaryPath: null));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the missing-binary status line");
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Skew_missing_profile_precondition_fails_status_only_no_prompt() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.ProfileName = null;
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the missing-profile status line");
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    // ---- harness ----

    /// Records every MutationRequest the controller hands to `_runMutation` and lets a test
    /// script an outcome per request (blanket Behavior, TCS-friendly) — the fake lane seam Task
    /// 10's controller tests drive instead of a raw IKcapCli mutation call. Defaults to an
    /// immediate Succeeded so tests that don't care about the mutation's own outcome (most of the
    /// "no mutation happens" tests never even call this) aren't forced to script one.
    sealed class FakeMutationLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    sealed class Harness : IAsyncDisposable {
        public readonly FakeDaemonClientService Client = new();
        public readonly FakeKcapCli Cli = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly string TempDir = Directory.CreateTempSubdirectory("kcap-lifecycle-").FullName;
        public readonly AppStateStore Store;
        public readonly FakeMutationLane Lane = new();
        public readonly DaemonLifecycleController Controller;
        public readonly string? CanonicalServer;

        public string? ProfileName = "default";

        public Harness(string? canonicalServer = "https://kcap.example.com:443", bool autoActionsPermanentlyClosed = false) {
            CanonicalServer = canonicalServer;
            Time  = new TimerCountingTimeProvider(Clock);
            Store = new AppStateStore(Path.Combine(TempDir, "app-state.json"));
            Controller = new DaemonLifecycleController(
                Client, Cli, Probe, Store, Surface, () => Task.FromResult<string?>(ProfileName), Time,
                CanonicalServer, Lane.RunAsync, autoActionsPermanentlyClosed);
        }

        public void Start() => Controller.Start();

        public void PushConnected() =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));

        public void PushUnreachable(string reason = "daemon_unreachable", string? daemonVersion = null) =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null, daemonVersion));

        public void PushSnapshot(string version) =>
            Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(version: version));

        public async ValueTask DisposeAsync() {
            await Controller.DisposeAsync();
            try { Directory.Delete(TempDir, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }
}

// TimerCountingTimeProvider is shared from ConsentServiceTests.cs (same namespace).

/// Scripted IKcapCli — every member is a settable behavior func plus a call counter, so tests
/// can drive both immediate results and TaskCompletionSource-controlled hangs (the once-per-run
/// arm test) without touching a real process. Task 10: StartVerified/InstallVerified/
/// DetachedStart are no longer called by the controller AT ALL (routed through the lane instead)
/// — their counters stay wired up purely as a regression tripwire (every controller test asserts
/// they remain 0).
sealed class FakeKcapCli : IKcapCli {
    public string? CliPath { get; set; } = "/opt/kcap/bin/kcap";

    public int VersionCallCount;
    public Func<CancellationToken, Task<string?>> VersionBehavior = _ => Task.FromResult<string?>("1.0.0");
    public Task<string?> VersionAsync(CancellationToken ct) {
        VersionCallCount++;
        return VersionBehavior(ct);
    }

    public int StatusCallCount;
    public Func<CancellationToken, Task<ServiceSnapshot?>> StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
    public Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) {
        StatusCallCount++;
        return StatusBehavior(ct);
    }

    public int StartVerifiedCallCount;
    public Func<CancellationToken, Task<ProcessResult>> StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) {
        StartVerifiedCallCount++;
        return StartVerifiedBehavior(ct);
    }

    public int InstallVerifiedCallCount;
    public bool? LastInstallReplace;
    public Func<bool, CancellationToken, Task<ProcessResult>> InstallVerifiedBehavior =
        (_, _) => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct) {
        InstallVerifiedCallCount++;
        LastInstallReplace = replace;
        return InstallVerifiedBehavior(replace, ct);
    }

    public int DetachedStartCallCount;
    public Func<CancellationToken, Task<ProcessResult>> DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));

    public string? LastBootAttemptId;
    public Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct) {
        DetachedStartCallCount++;
        LastBootAttemptId = bootAttemptId;
        return DetachedStartBehavior(ct);
    }
}

/// Scripted ILoginShellProbe — the controller only ever calls TerminalPathAsync (the install
/// precondition); KcapOnPathAsync is used by PathShimInstaller/ShimOfferCoordinator tests.
/// KcapOnPathFreshBehavior, when set, answers a forceRefresh=true call distinctly from the cached
/// KcapOnPathBehavior — otherwise a forced call just falls back to KcapOnPathBehavior too.
sealed class FakeLoginShellProbe : ILoginShellProbe {
    public Func<CancellationToken, Task<string?>> TerminalPathBehavior = _ => Task.FromResult<string?>("/usr/bin:/bin");
    public Task<string?> TerminalPathAsync(CancellationToken ct) => TerminalPathBehavior(ct);

    public Func<CancellationToken, Task<bool?>> KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);
    public Func<CancellationToken, Task<bool?>>? KcapOnPathFreshBehavior;
    public int KcapOnPathForceRefreshCallCount;
    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) {
        if (forceRefresh) {
            KcapOnPathForceRefreshCallCount++;
            return (KcapOnPathFreshBehavior ?? KcapOnPathBehavior)(ct);
        }
        return KcapOnPathBehavior(ct);
    }

    public Func<CancellationToken, Task<string?>> KcapPathBehavior = _ => Task.FromResult<string?>(null);
    public Func<CancellationToken, Task<string?>>? KcapPathFreshBehavior;
    public readonly List<bool> KcapPathForceRefreshCalls = [];
    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) {
        KcapPathForceRefreshCalls.Add(forceRefresh);
        return forceRefresh ? (KcapPathFreshBehavior ?? KcapPathBehavior)(ct) : KcapPathBehavior(ct);
    }
}
