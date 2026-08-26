using Capacitor.Cli.Core;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// spec §3 step 7: every row of the lifecycle slice's state matrix → the exact mutation verb (or
/// an explicit no-mutation) and the affordance offered, plus the decision-7 claim application
/// rules. The lane is a recording delegate (results are waiter-state-only), the claims store is
/// REAL on temp paths (so TryConsume's own two-lock compare runs), and the surface records every
/// dialog — the single-presentation rule is asserted by the surface staying untouched on every
/// waiter outcome. Every "no claim application" assertion checks GetCalls, not just PutV2Calls:
/// the GET is the FIRST ops touch, so a guard that stopped guarding would be invisible otherwise.
public class DaemonStepViewModelTests {
    const string Profile    = "default";
    const string RawServer  = "https://example.test";
    const string DaemonName = "kcap-daemon";
    static readonly string CanonicalServer = ServerIdentity.Canonicalize(RawServer)!;

    static ServiceSnapshot Snap(
            string state = "not_installed", bool unitPresent = false, int? jobPid = null, int? daemonPid = null,
            bool txnMarker = false, bool txnActive = false, string? installBinaryPath = "/opt/kcap/kcap-daemon") =>
        new(DaemonName, unitPresent, state, "/opt/kcap/kcap-daemon", installBinaryPath, jobPid, daemonPid,
            txnMarker, txnActive);

    static ObservedEvidence Evidence(
            string? server = RawServer, string? name = DaemonName, bool reachable = true, bool consistent = true,
            params string[] capabilities) =>
        new(reachable, capabilities.Length == 0 ? ["consent/3"] : capabilities, "0.12.0-beta.1", server, name,
            4242, "instance-1", consistent);

    static ConsentPolicyDto Policy(string @default = "allow", params ConsentRuleDto[] rules) =>
        new(@default, 300, [.. rules]);

    /// Records every request the step routes through the lane and answers with the scripted
    /// outcome — the lane's own result IS the step's success predicate.
    sealed class RecordingLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    sealed class ScriptedObservation : IDaemonObservation {
        readonly Queue<ObservedEvidence?> _queued = new();

        public int Calls;
        public readonly List<MutationRequest> Requests = [];
        public ObservedEvidence? Default;

        public void Queue(ObservedEvidence? evidence) => _queued.Enqueue(evidence);

        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : Default);
        }
    }

    sealed class Harness : IDisposable {
        readonly TempConfigRoot _config = new();

        public readonly FakeKcapCli Cli = new();
        public readonly RecordingLane Lane = new();
        public readonly ScriptedObservation Observation = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly ConsentFlipClaims Claims;
        public readonly DaemonStepViewModel Vm;

        public (string Profile, string Server, string DaemonName)? Identity = (Profile, RawServer, DaemonName);
        public (string Profile, string Server, string DaemonName) UnderConfigLock = (Profile, RawServer, DaemonName);
        public string? TerminalPath = "/usr/bin:/bin";

        public Harness() {
            Time   = new TimerCountingTimeProvider(Clock);
            Claims = new ConsentFlipClaims(_config.Root);
            Vm = new DaemonStepViewModel(
                Cli, Lane.RunAsync, () => Identity, Observation, Ops, Claims, () => UnderConfigLock, Surface,
                _ => Task.FromResult<string?>(TerminalPath), Time);
        }

        public void ArmClaim() => Claims.Arm(new ConsentFlipClaim(Profile, CanonicalServer));

        public void Status(ServiceSnapshot? snapshot) => Cli.StatusBehavior = _ => Task.FromResult(snapshot);

        public Task Enter() => Vm.OnEnterAsync(CancellationToken.None);

        public Task Act() => Vm.RunActionAsync();

        public void Dispose() => _config.Dispose();
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    // ── gate / precondition rows ────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_committed_sign_in_the_step_offers_nothing_and_never_reads_status() {
        var (row, affordance, message, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Identity = null;

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.RequiresSignIn);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.RequiresSignInMessage);
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_resolved_CLI_the_step_offers_nothing() {
        var (row, affordance, message, statusCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Cli.CliPath = null;

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Cli.StatusCallCount);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.CliMissing);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.CliMissingMessage);
        await Assert.That(statusCalls).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_server_that_cannot_be_canonicalized_refuses_before_any_status_read() {
        var (row, affordance, message, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Identity = (Profile, "file:///etc/passwd", DaemonName);

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NoServerConfigured);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.NoServerMessage); // humanized, not the raw token
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Re_entering_after_a_completed_sign_in_classifies_on_the_now_resolvable_identity() {
        var (firstRow, secondRow, secondAffordance) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Identity = null;

            await h.Enter();
            var first = h.Vm.Row;

            h.Identity = (Profile, RawServer, DaemonName); // the Sign in step committed while we were away
            await h.Enter();

            return (first, h.Vm.Row, h.Vm.Affordance);
        });

        await Assert.That(firstRow).IsEqualTo(DaemonRow.RequiresSignIn);
        await Assert.That(secondRow).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(secondAffordance).IsEqualTo(DaemonAffordance.Install);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unresolvable_daemon_binary_withdraws_the_unit_writing_offer() {
        var (row, affordance, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(installBinaryPath: null));

            await h.Enter();
            await h.Act(); // no affordance — nothing to run

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.BinaryUnresolved);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.BinaryUnresolvedMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unresolvable_daemon_binary_still_allows_the_start_row() {
        var (row, affordance) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "installed", unitPresent: true, installBinaryPath: null));

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.Stopped); // starting an installed unit writes none
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Start);
    }

    // ── unreadable / unrecognized evidence: honest message, no mutation ─────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreadable_status_is_an_honest_message_with_no_mutation() {
        var (row, affordance, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(null);

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.StatusUnknown);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.StatusUnknownMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unrecognized_service_state_never_reads_as_not_installed() {
        var (row, affordance, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "some_future_state"));

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.StatusUnknown);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.UnrecognizedStateMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    // ── install / start rows ────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_unit_and_no_live_daemon_offers_install_and_routes_Install_through_the_lane() {
        var (row, affordance, requests, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());

            await h.Enter();
            var offered = (h.Vm.Row, h.Vm.Affordance);
            await h.Act();

            return (offered.Row, offered.Affordance, h.Lane.Requests.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Install);
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(requests[0].Profile).IsEqualTo(Profile);
        await Assert.That(requests[0].CanonicalServer).IsEqualTo(CanonicalServer);
        await Assert.That(requests[0].DaemonName).IsEqualTo(DaemonName);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    [Arguments("installed")]    // loaded label, inactive job
    [Arguments("not_installed")] // unit on disk, no loaded label
    [NotInParallel("AvaloniaSession")]
    public async Task A_present_unit_with_no_live_daemon_offers_a_verified_start(string state) {
        var (row, affordance, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: state, unitPresent: true));

            await h.Enter();
            var offered = (h.Vm.Row, h.Vm.Affordance);
            await h.Act();

            return (offered.Row, offered.Affordance, h.Lane.Requests.ToList());
        });

        await Assert.That(row).IsEqualTo(DaemonRow.Stopped);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Start);
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_job_with_no_daemon_evidence_offers_nothing() {
        var (row, affordance, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100));

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.RunningUnconfirmed);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(mutations).IsEqualTo(0);
    }

    // ── ownership rows ──────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Positive_ownership_with_an_identity_match_is_already_enabled_and_applies_the_claim() {
        var (row, affordance, satisfied, mutations, putPayloads, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Satisfied, h.Lane.Requests.Count,
                h.Ops.PutV2Payloads.ToList(), h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.AlreadyEnabled);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(satisfied).IsTrue();
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(putPayloads.Count).IsEqualTo(1);
        await Assert.That(putPayloads[0].ExpectedName).IsEqualTo(DaemonName);
        await Assert.That(putPayloads[0].ExpectedServerUrl).IsEqualTo(CanonicalServer);
        await Assert.That(putPayloads[0].Policy.Default).IsEqualTo("prompt");
        await Assert.That(pending).IsEqualTo(0); // consumed through the real two-lock clear
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_step_put_preserves_the_daemons_existing_rules() {
        var payload = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy("allow", new ConsentRuleDto("deny", "someone", null, null, null)));
            h.Ops.QueuePutV2(true, null);

            await h.Enter();

            return h.Ops.PutV2Payloads.Single();
        });

        await Assert.That(payload.Policy.Rules.Count).IsEqualTo(1);
        await Assert.That(payload.Policy.PromptTimeoutSeconds).IsEqualTo(300);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Ownership_without_an_identity_match_offers_only_the_takeover_and_never_the_claim() {
        var (row, affordance, satisfied, mutations, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence(server: "https://other.test");

            await h.Enter();
            await h.Act(); // the surface declines by default

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Satisfied, h.Lane.Requests.Count, h.Ops.GetCalls,
                h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.OwnedIdentityMismatch);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Takeover);
        await Assert.That(satisfied).IsFalse();
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(gets).IsEqualTo(0); // the claim path was never entered at all
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unverifiable_daemon_identity_fails_closed_to_the_takeover_row() {
        var row = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence(consistent: false); // the two probe dials disagree

            await h.Enter();

            return h.Vm.Row;
        });

        await Assert.That(row).IsEqualTo(DaemonRow.OwnedIdentityMismatch);
    }

    // ── manual-daemon rows ──────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_identity_matched_manual_daemon_offers_a_disclosed_takeover_that_replaces_on_accept() {
        var (row, affordance, prompts, requests, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "not_installed", daemonPid: 777));
            h.Observation.Default = Evidence();
            h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

            await h.Enter();
            var offered = (h.Vm.Row, h.Vm.Affordance);
            await h.Act();

            return (offered.Row, offered.Affordance, h.Surface.Prompts.ToList(), h.Lane.Requests.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.ManualIdentityMatch);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Takeover);
        await Assert.That(prompts.Count).IsEqualTo(1);
        await Assert.That(prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Declining_the_manual_takeover_leaves_the_step_incomplete_but_still_applies_the_claim() {
        var (satisfied, mutations, putPayloads, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "not_installed", daemonPid: 777));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Enter();
            await h.Act(); // declines

            return (h.Vm.Satisfied, h.Lane.Requests.Count, h.Ops.PutV2Payloads.ToList(), h.Claims.Pending().Count);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(putPayloads.Count).IsEqualTo(1);
        await Assert.That(putPayloads[0].ExpectedServerUrl).IsEqualTo(CanonicalServer);
        await Assert.That(pending).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_manual_daemon_for_another_server_mutates_nothing_and_applies_no_claim() {
        var (row, affordance, message, mutations, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "not_installed", daemonPid: 777));
            h.Observation.Default = Evidence(server: "https://other.test");

            await h.Enter();
            await h.Act(); // declines

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Lane.Requests.Count, h.Ops.GetCalls,
                h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.ManualIdentityMismatch);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Takeover); // the only offered action
        await Assert.That(message).Contains("https://other.test");
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(gets).IsEqualTo(0); // the decline path never enters the claim path on a mismatch
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    // ── repair rows ─────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_orphan_label_offers_the_repair_affordance() {
        var (row, affordance, prompts, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "installed", unitPresent: false));
            h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

            await h.Enter();
            var offered = (h.Vm.Row, h.Vm.Affordance);
            await h.Act();

            return (offered.Row, offered.Affordance, h.Surface.Prompts.ToList(), h.Lane.Requests.ToList());
        });

        await Assert.That(row).IsEqualTo(DaemonRow.OrphanLabel);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Repair);
        await Assert.That(prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_transaction_marker_offers_repair_instead_of_a_blind_reinstall() {
        var (row, affordance, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(txnMarker: true));

            await h.Enter();

            return (h.Vm.Row, h.Vm.Affordance, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.StaleMarker);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Repair);
        await Assert.That(mutations).IsEqualTo(0);
    }

    // ── txn_active: wait, never a parallel mutation ─────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_transaction_is_waited_out_and_the_matrix_proceeds_once_it_clears() {
        var (row, affordance, statusCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var call = 0;
            h.Cli.StatusBehavior = _ => {
                call++;
                return Task.FromResult<ServiceSnapshot?>(call == 1 ? Snap(txnActive: true) : Snap());
            };

            var enter = h.Enter();
            await WaitUntilAsync(() => h.Time.TimersCreated >= 1, "the txn-active poll timer to be armed");
            await Assert.That(h.Lane.Requests).IsEmpty(); // never mutated into the held flock
            h.Clock.Advance(DaemonStepViewModel.TxnPollInterval);
            await enter;

            return (h.Vm.Row, h.Vm.Affordance, h.Cli.StatusCallCount);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.Install);
        await Assert.That(statusCalls).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_transaction_that_never_clears_ends_in_an_honest_message_with_no_mutation() {
        var (row, affordance, message, mutations, statusCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(txnActive: true));

            var enter = h.Enter();
            for (var poll = 0; poll < DaemonStepViewModel.MaxTxnPolls && !enter.IsCompleted; poll++) {
                await WaitUntilAsync(() => h.Time.TimersCreated >= poll + 1, "the next txn-active poll timer");
                h.Clock.Advance(DaemonStepViewModel.TxnPollInterval);
            }
            await enter;

            return (h.Vm.Row, h.Vm.Affordance, h.Vm.Message, h.Lane.Requests.Count, h.Cli.StatusCallCount);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.TransactionActive);
        await Assert.That(affordance).IsEqualTo(DaemonAffordance.None);
        await Assert.That(message).IsEqualTo(DaemonStepViewModel.TxnActiveMessage);
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(statusCalls).IsEqualTo(DaemonStepViewModel.MaxTxnPolls + 1);
    }

    // ── outcome handling: state only, never a second presentation ───────────

    [Test]
    [Arguments("succeeded", true)]
    [Arguments("succeeded_after_timeout", true)]
    [Arguments("unconfirmed", false)]
    [Arguments("attention_skew", false)]
    [Arguments("attention_repair", false)]
    [Arguments("refused", false)]
    [Arguments("failed", false)]
    [NotInParallel("AvaloniaSession")]
    public async Task Satisfied_follows_the_lanes_own_outcome_and_nothing_else(string outcomeKey, bool expected) {
        var (satisfied, status, prompts, statusLines, attentionLines) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(outcomeKey switch {
                "succeeded"               => new MutationOutcome.Succeeded(),
                "succeeded_after_timeout" => new MutationOutcome.SucceededAfterTimeout(),
                "unconfirmed"             => new MutationOutcome.UnconfirmedNoAttach(),
                "attention_skew"          => new MutationOutcome.AttentionSkew("daemon_below_floor"),
                "attention_repair"        => new MutationOutcome.AttentionRepair("stale_marker"),
                "refused"                 => new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention),
                _                         => new MutationOutcome.Failed(28, "identity_mismatch", RecoverySurface.Takeover),
            });

            await h.Enter();
            await h.Act();

            return (h.Vm.Satisfied, h.Vm.Status, h.Surface.Prompts.Count, h.Surface.StatusMessages.Count,
                h.Surface.AttentionMessages.Count);
        });

        await Assert.That(satisfied).IsEqualTo(expected);
        await Assert.That(status).IsNotNull();
        // Single presentation: the waiter never opens a dialog and never touches the surface —
        // actionable outcomes reach the user through the channel consumer instead.
        await Assert.That(prompts).IsEqualTo(0);
        await Assert.That(statusLines).IsEqualTo(0);
        await Assert.That(attentionLines).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_outcome_names_its_coded_reason() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Lane.Behavior = (_, _) =>
                Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(24, null, RecoverySurface.Attention));

            await h.Enter();
            await h.Act();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains("verify_readiness_timeout");
    }

    // ── claim application after a mutation ──────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_install_applies_the_pending_claim_against_the_daemon_it_just_verified() {
        var (putPayloads, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Enter();
            await h.Act();

            return (h.Ops.PutV2Payloads.ToList(), h.Claims.Pending().Count);
        });

        await Assert.That(putPayloads.Count).IsEqualTo(1);
        await Assert.That(putPayloads[0].ExpectedName).IsEqualTo(DaemonName);
        await Assert.That(pending).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_without_consent_3_gets_no_put_and_keeps_the_claim_pending() {
        var (gets, puts, pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence(capabilities: ["consent/1"]);

            await h.Enter();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(gets).IsEqualTo(0);
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonStepViewModel.ClaimMissingCapabilityMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rejected_ack_retains_the_claim_and_surfaces_repair_guidance() {
        var (pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(false, "identity_mismatch");

            await h.Enter();

            return (h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonStepViewModel.ClaimFailedMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ops_failure_retains_the_claim() {
        var (pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2Failure("daemon_unreachable");

            await h.Enter();

            return (h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonStepViewModel.ClaimFailedMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_claim_for_another_server_is_never_applied() {
        var (gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Claims.Arm(new ConsentFlipClaim(Profile, ServerIdentity.Canonicalize("https://other.test")!));
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();

            await h.Enter();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(gets).IsEqualTo(0); // the pending filter, not a later rejection, is what stops this
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_mutation_whose_re_probe_no_longer_matches_applies_no_claim() {
        var (satisfied, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap());
            h.Observation.Default = Evidence(server: "https://other.test"); // the post-mutation re-check fails

            await h.Enter();
            await h.Act();

            return (h.Vm.Satisfied, h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(satisfied).IsTrue(); // the lane's own outcome still decides enablement
        await Assert.That(gets).IsEqualTo(0);  // but the claim never reaches a daemon we did not just verify
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_operator_chosen_deny_is_left_stricter_than_prompt() {
        var (gets, puts, pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy("deny"));

            await h.Enter();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(gets).IsEqualTo(1);
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1); // retained, inert
        await Assert.That(status).IsEqualTo(DaemonStepViewModel.ClaimAlreadyStricterMessage);
    }

    // ── navigation ──────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_mid_mutation_detaches_the_waiter_and_never_vetoes() {
        var (observedCancel, canLeave, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Lane.Behavior = async (_, ct) => {
                entered.TrySetResult(true);
                // The lane owns the child: only the WAITER's token is cancelled here.
                await using var registration = ct.Register(() => cancelled.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, ct);
                return new MutationOutcome.Succeeded();
            };

            await h.Enter();
            var action = h.Act();
            await entered.Task;

            var left = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            await action;

            return (cancelled.Task.IsCompleted, left, h.Vm.Status);
        });

        await Assert.That(observedCancel).IsTrue();
        await Assert.That(canLeave).IsTrue();
        await Assert.That(status).IsEqualTo(DaemonStepViewModel.DetachedMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_awaits_an_in_flight_claim_put_rather_than_cancelling_it() {
        var (pending, puts) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100));
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            var put = h.Ops.ArmPutV2();

            var enter = h.Enter();
            await WaitUntilAsync(() => h.Ops.PutV2Calls == 1, "the conditional put to reach the ops layer");

            var leaving = h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            put.SetResult(new ConsentAckDto(true, null, null));
            await Assert.That(await leaving).IsTrue();
            await enter;

            return (h.Claims.Pending().Count, h.Ops.PutV2Calls);
        });

        await Assert.That(pending).IsEqualTo(0); // the put completed and the claim was consumed
        await Assert.That(puts).IsEqualTo(1);
    }
}

/// Template smoke coverage, mirroring the sibling step tests: the daemon step's named controls
/// resolve through the real window.
public class DaemonStepTemplateTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_selects_a_template_for_the_daemon_step() {
        var (actionButton, refreshButton, messageText) = await AvaloniaSession.DispatchAsync(async () => {
            using var temp = new TempClaims();
            var step = new DaemonStepViewModel(
                new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
                    new ServiceSnapshot("kcap-daemon", false, "not_installed", null, "/opt/kcap/kcap-daemon", null, null, false, false)) },
                (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
                () => ("default", "https://example.test", "kcap-daemon"),
                new NeverObserved(), new ScriptedLocalControlOps(), temp.Claims,
                () => ("default", "https://example.test", "kcap-daemon"), new FakeLifecycleSurface(),
                _ => Task.FromResult<string?>("/usr/bin"), TimeProvider.System);

            var vm = new OnboardingViewModel([step, new DoneStepViewModel(() => [])]);
            await vm.PendingEnterForTesting;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var action  = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DaemonActionButton");
            var refresh = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DaemonRefreshButton");
            var message = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "DaemonMessageText");

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (action, refresh, message?.Text);
        });

        await Assert.That(actionButton).IsNotNull();
        await Assert.That(actionButton!.Content).IsEqualTo("Enable daemon");
        await Assert.That(refreshButton).IsNotNull();
        await Assert.That(messageText).IsEqualTo(DaemonStepViewModel.NotInstalledMessage);
    }

    sealed class NeverObserved : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) =>
            Task.FromResult<ObservedEvidence?>(null);
    }

    sealed class TempClaims : IDisposable {
        readonly TempConfigRoot _config = new();

        public readonly ConsentFlipClaims Claims;

        public TempClaims() =>
            Claims = new ConsentFlipClaims(_config.Root);

        public void Dispose() => _config.Dispose();
    }
}
