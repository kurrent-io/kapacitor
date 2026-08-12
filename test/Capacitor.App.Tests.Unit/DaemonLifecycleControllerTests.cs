using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Startup phase, reconciliation, and the §4.2 startup matrix (spec). Every
/// clock-dependent wait goes through FakeTimeProvider (never Task.Delay-based ordering);
/// settling between an event push and its effect is driven by WaitUntilAsync polling on the
/// fakes' call counters (PauseControllerTests/ConsentServiceTests idiom).
public class DaemonLifecycleControllerTests {
    static readonly TimeSpan ConfirmWindow         = DaemonLifecycleController.ConfirmWindow;
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

    static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);
    static ProcessResult Failed(int exitCode, string stderr) => new(exitCode, "", stderr, false);

    // ---- startup matrix rows (§4.2) ----

    [Test]
    public async Task Row1_job_running_is_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the matrix status query");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row2_loaded_inactive_plist_present_daemonPid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "service start --verify");
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row2b_loaded_inactive_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row3_orphan_label_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the orphan-label attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row4_no_label_plist_present_daemonPid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "service start --verify (bootstrap)");
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row4b_no_label_plist_present_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed", daemonPid: 777));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row5_nothing_preconditions_pass_installs_verified_without_replace() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "service install --verify");
        await Assert.That(h.Cli.LastInstallReplace).IsNotNull().And.IsEqualTo(false);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // Standards-2: an unrecognized wire state must never fall through to the NotInstalled
    // (auto-install/start) branch — positive evidence only.
    [Test]
    public async Task Unrecognized_state_is_status_only_no_auto_install_or_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest unrecognized-state line");
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row4_starts_verified_even_when_terminal_PATH_is_unknown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "service start --verify despite unknown PATH");
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row5_nothing_installBinaryPath_null_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(installBinaryPath: null));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-install-binary line");
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0); // deferred — not mutated into the held flock

        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the one bounded requery");
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the matrix proceeding once the flock cleared");
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
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

    // ---- UX confirmation ----

    [Test]
    public async Task Successful_mutation_with_fresh_connected_within_window_no_status_message() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.StartVerifiedBehavior = _ => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "the start-verify call to begin");

        // A Connected racing the still-in-flight CLI call must still count as "after the
        // mutation began" — the confirm waiter is armed before `mutate` runs.
        h.PushConnected();
        release.SetResult(Ok());

        await WaitUntilAsync(() => h.Client.RestartCount >= 1, what: "the post-mutation reattach kick");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing retry message every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }

    [Test]
    public async Task Successful_mutation_without_fresh_connected_times_out_to_retrying_status() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Cli.StartVerifiedBehavior = _ => Task.FromResult(Ok());
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "the start-verify call");
        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the confirm-window timer to be armed");
        h.Clock.Advance(ConfirmWindow);

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the retrying status line");
        await Assert.That(h.Surface.StatusMessages[0]).Contains("retrying");
        // No rollback call exists on IKcapCli at all — assert no other call happened.
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);
    }

    // ---- coded failure ----

    [Test]
    public async Task Coded_failure_surfaces_the_verify_token_once() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Cli.StartVerifiedBehavior = _ => Task.FromResult(Failed(24, "verify_readiness_timeout: gave up waiting"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the coded-failure status line");
        await Assert.That(h.Surface.StatusMessages[0]).Contains("verify_readiness_timeout");

        h.PushUnreachable(); // once-per-run: no retry
        await Task.Delay(50);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
    }

    // ---- Finding 8: forced-kill timeout is never read as a verify outcome (spec §3.6) ----

    [Test]
    public async Task TimedOut_mutation_reQueries_status_and_raises_attention_not_a_status_line() {
        await using var h = new Harness();
        var statusCalls = 0;
        h.Cli.StatusBehavior = _ => {
            statusCalls++;
            // First call is the matrix's own classification query (Row5: nothing installed →
            // install). Second is RunVerifiedMutationAsync's post-timeout re-query.
            return Task.FromResult<ServiceSnapshot?>(statusCalls == 1 ? Snap() : Snap(txnMarker: true, txnActive: true));
        };
        // ExitCode 0 here is deliberate: a killed tree's exit code must never be trusted as Ok.
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(new ProcessResult(0, "", "killed", true));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the install attempt");
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the timeout attention");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("txn_marker=True");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("txn_active=True");
        await Assert.That(h.Surface.StatusMessages).IsEmpty(); // never a plain exit-code Status line
        await Assert.That(statusCalls).IsEqualTo(2);
    }

    [Test]
    public async Task TimedOut_mutation_never_kicks_reattach_or_waits_for_confirmation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Cli.StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(0, "", "killed", true));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the timeout attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    [Test]
    public async Task TimedOut_mutation_status_reQuery_failure_still_raises_attention() {
        await using var h = new Harness();
        var statusCalls = 0;
        h.Cli.StatusBehavior = _ => {
            statusCalls++;
            return Task.FromResult<ServiceSnapshot?>(statusCalls == 1 ? Snap() : null);
        };
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(new ProcessResult(1, "", "killed", true));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the timeout attention despite an unreadable re-query");
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
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
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.StartVerifiedBehavior = _ => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "the in-flight mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        // A coded failure skips the confirm-wait entirely (no fake-clock advance needed here —
        // that phase is covered by the UX-confirmation tests above) so the gate releases as soon
        // as the CLI child itself finishes.
        release.SetResult(Failed(24, "verify_readiness_timeout"));
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_nothing_at_all_falls_back_to_detached_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(1);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_loaded_plist_present_pid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.StartVerifiedBehavior = _ => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "service start --verify");
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);

        // A coded failure skips the confirm-wait entirely (the UX-confirmation tests above already
        // cover that phase) so this call — and the test — can complete without a clock advance.
        release.SetResult(Failed(21, "verify_viability"));
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_orphan_label_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_no_label_plist_present_pid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.StartVerifiedBehavior = _ => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "service start --verify (bootstrap)");
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);

        release.SetResult(Failed(21, "verify_viability"));
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
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_repair_accept_calls_install_verified_replace_true_same_helper_as_takeover() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.InstallVerifiedBehavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the repair install");
        await Assert.That(h.Cli.LastInstallReplace).IsNotNull().And.IsEqualTo(true);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);

        release.SetResult(Failed(21, "verify_viability"));
        await startTask;
    }

    [Test]
    public async Task StartAction_repair_decline_does_not_mutate() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_status_unknown_takes_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    // Standards-2 counterpart for the Start action's own state-classification switch.
    [Test]
    public async Task StartAction_unrecognized_state_is_status_only_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);
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
        var install = new TaskCompletionSource<ProcessResult>();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — the install row
        h.Cli.InstallVerifiedBehavior = (_, _) => install.Task;
        h.Start();

        h.PushUnreachable(); // claims the arm, starts the startup matrix, blocks on the install call
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the startup install to begin (holding the gate)");
        var statusCallsBeforeStart = h.Cli.StatusCallCount;

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await Task.Delay(50); // give a wrongly-unblocked Start every chance to act early
        await Assert.That(startTask.IsCompleted).IsFalse();
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCallsBeforeStart); // no re-query yet — still blocked

        // A coded failure skips RunVerifiedMutationAsync's confirm-wait entirely (no fake-clock
        // advance needed — that phase is covered by the UX-confirmation tests), so the gate
        // releases as soon as this CLI child finishes.
        install.SetResult(Failed(21, "verify_viability"));
        await startTask;

        await Assert.That(h.Cli.StatusCallCount).IsGreaterThan(statusCallsBeforeStart); // a fresh query after the gate cleared
    }

    [Test]
    public async Task QuiescedAsync_waits_for_a_startAction_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<ProcessResult>();
        h.Cli.StartVerifiedBehavior = _ => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Cli.StartVerifiedCallCount == 1, what: "the Start-triggered mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        release.SetResult(Failed(24, "verify_readiness_timeout"));
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
        // test's PhaseClosed wait on the fake-clock confirm window, which nothing here advances.
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
    public async Task Skew_accept_calls_install_verified_replace_true_and_nothing_else() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(Ok());
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");

        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the takeover install");
        await Assert.That(h.Cli.LastInstallReplace).IsNotNull().And.IsEqualTo(true);
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.DetachedStartCallCount).IsEqualTo(0);
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
            client2, cli2, new FakeLoginShellProbe(), h.Store, surface2, () => Task.FromResult<string?>("default"), h.Time);
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
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);

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
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);

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
    // abort above. Distinct from Skew_accept_calls_install_verified_replace_true_and_nothing_else:
    // this one asserts the revalidation query itself ran (a StatusCallCount delta of 2) rather than
    // just the eventual install call.
    [Test]
    public async Task Skew_accept_with_unchanged_status_revalidates_then_proceeds() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var installedSnap = Snap(unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd");
        var noMutationRow = Snap(state: "running", jobPid: 1, daemonPid: 1);
        var next = noMutationRow;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(next);
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(Ok());
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

        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the takeover install");
        await Assert.That(h.Cli.StatusCallCount - statusCallsBeforeSkew).IsEqualTo(2); // the dialog query + the in-gate revalidation

        h.PushConnected(); // let the mutation's confirm-wait resolve so disposal doesn't wait on it
    }

    [Test]
    public async Task Skew_accept_coded_failure_retracts_claim_and_clears_run_flag() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(Failed(21, "verify_viability: nope"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the takeover install attempt");
        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the coded-failure status line");

        var afterFailure = await h.Store.LoadAsync();
        await Assert.That((afterFailure.DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0")).IsFalse();

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
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromResult(Ok());
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the takeover install");

        await WaitUntilAsync(
            () => !(h.Store.LoadAsync().GetAwaiter().GetResult().DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0"),
            what: "the claim retracted on acceptance");

        // Let the mutation actually resolve (it awaits a fresh Connected or the never-advanced
        // fake-clock confirm window otherwise) so disposal doesn't wait on it.
        h.PushConnected();
    }

    [Test]
    public async Task Skew_accept_retracts_the_claim_even_when_the_mutation_throws() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        // Simulates a shutdown mid-spawn or a process/IO fault — RunVerifiedMutationAsync does
        // not catch around the CLI call, so this propagates out of RunSkewCheckAsync's mutation
        // step entirely.
        h.Cli.InstallVerifiedBehavior = (_, _) => Task.FromException<ProcessResult>(new OperationCanceledException("shutdown mid-install"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await WaitUntilAsync(() => h.Cli.InstallVerifiedCallCount == 1, what: "the takeover install attempt");

        await WaitUntilAsync(
            () => !(h.Store.LoadAsync().GetAwaiter().GetResult().DeclinedTakeoverPairs ?? []).Contains("0.9|1.0.0"),
            what: "the claim retracted despite the mutation throwing");

        // A fresh controller sharing the same store re-offers the same pair — accepted-but-failed
        // must never read back as "the user declined".
        var surface2 = new FakeLifecycleSurface();
        var client2  = new FakeDaemonClientService();
        var cli2     = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed")) };
        await using var controller2 = new DaemonLifecycleController(
            client2, cli2, new FakeLoginShellProbe(), h.Store, surface2, () => Task.FromResult<string?>("default"), h.Time);
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

    sealed class Harness : IAsyncDisposable {
        public readonly FakeDaemonClientService Client = new();
        public readonly FakeKcapCli Cli = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly string TempDir = Directory.CreateTempSubdirectory("kcap-lifecycle-").FullName;
        public readonly AppStateStore Store;
        public readonly DaemonLifecycleController Controller;

        public string? ProfileName = "default";

        public Harness() {
            Time  = new TimerCountingTimeProvider(Clock);
            Store = new AppStateStore(Path.Combine(TempDir, "app-state.json"));
            Controller = new DaemonLifecycleController(
                Client, Cli, Probe, Store, Surface, () => Task.FromResult<string?>(ProfileName), Time);
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
/// arm test) without touching a real process.
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
    public Task<ProcessResult> DetachedStartAsync(CancellationToken ct) {
        DetachedStartCallCount++;
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
}
