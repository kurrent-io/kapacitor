using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Round-dispatch grace (server-side design doc
/// docs/superpowers/specs/2026-08-10-ai1842-round-dispatch-grace-design.md in kcap-server), the two
/// halves that sit on top of the delivered-input clock advance (see SendInputActivityClockTests.cs):
///
/// <para><b>(1) The immediate out-of-cycle report.</b> One <c>DaemonStatusReport</c> per successfully
/// handled input invocation, so the server sees the fresh attestation at dispatch time instead of up
/// to a full 60s report cadence later. The wire carries no round/dispatch identity
/// (<see cref="SendInputCommand"/> is agent+text+attachments), so "per round" is not implementable
/// and duplicates are tolerated by design. The report carries NO correlation nonce — it must never
/// masquerade as an answer to a server-side correlated status request.</para>
///
/// <para><b>(2) The snapshot/send ordering section — load-bearing, not hygiene.</b> The server folds
/// every flow-participant report and treats ANY activity-seq regression as permanent corruption: the
/// fold latches <c>Regressed</c> and disables liveness supervision for that agent id forever. A report
/// whose CONTENT was captured before a delivery advanced the clock but which is SENT after the
/// delivery-triggered report would present seq N after seq N+1 and trip exactly that latch — silently
/// disabling supervision, the opposite failure direction from the one this work exists to fix. So
/// every emission captures its snapshot AND completes its hub send under one per-daemon ordering
/// section, making captured content monotone in wire order (SignalR preserves per-connection send
/// order).</para>
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Delivered_input_emits_exactly_one_report_carrying_the_advanced_seq_and_reset_idle() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("dispatch-report-1", pty: new RecordingPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(server.StatusReportCount).IsEqualTo(0); // nothing has emitted yet

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));

        await PollUntilAsync(() => server.StatusReportCount >= 1);
        // Give a wrongly-duplicated emission a chance to land before pinning the count.
        await Task.Delay(50);
        await Assert.That(server.StatusReportCount).IsEqualTo(1);

        // The whole point of emitting HERE rather than waiting for the next 60s tick: the report the
        // server receives must already carry the post-delivery attestation. A snapshot captured before
        // the advance would still read seq 1 / idle 10_000.
        var entry = server.StatusReports[0].LiveAgents.Single(a => a.Id == agent.Id);
        await Assert.That(entry.ActivitySeq).IsEqualTo(2UL);
        await Assert.That(entry.IdleForMs).IsEqualTo(0UL);
    }

    /// <summary>The emission is gated on the SAME delivery success the clock advance is gated on: a
    /// failed write advances nothing, so there is no new attestation to announce and announcing the
    /// old one out of cycle would be pure noise (and, worse, a "the daemon is talking about this
    /// agent" signal for an agent whose write just failed).</summary>
    [Test]
    public async Task Failed_delivery_emits_no_report() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("dispatch-report-fail", pty: new AlwaysThrowsPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<IOException>(
            async () => await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null)));

        // The emission is fire-and-forget, so a wrongly-placed one would land shortly AFTER the throw
        // propagates — wait before asserting absence rather than reading the count immediately.
        await Task.Delay(100);
        await Assert.That(server.StatusReportCount).IsEqualTo(0);
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(1UL);
    }

    /// <summary>The ordering section, proven at its two load-bearing properties:
    ///
    /// <para>(i) it is held THROUGH the hub send, not merely through task creation — while a parked
    /// send holds it, a second emission cannot even reach the send (let alone capture a snapshot);</para>
    ///
    /// <para>(ii) send-completion order equals snapshot-capture order — the report that captured
    /// seq 1 completes first, so the server never folds seq 1 after seq 2.</para>
    ///
    /// Without the section this fails at BOTH: the delivery-triggered emission would capture seq 2 and
    /// complete immediately past the parked seq-1 send, delivering the regression that permanently
    /// latches the server's fold into <c>Regressed</c>.</summary>
    [Test]
    public async Task Report_content_is_monotone_in_send_completion_order() {
        var probe = new OrderingProbeServerConnection();
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var orch = BuildOrchestrator(probe, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("ordering-1", pty: new RecordingPtyProcess(), activityClock: clock);

        // The "slow periodic" emission: it captures the pre-delivery snapshot (seq 1) and then parks
        // INSIDE the hub send, exactly the window where a send that ignored the section could overtake it.
        var slowPeriodic = orch.SendDaemonStatusReportOnceAsync();
        await probe.FirstSendEntered.WaitAsync(TimeSpan.FromSeconds(5));

        // The delivery advances the clock to seq 2 and fires its own emission while that send is parked.
        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(2UL);

        // (i) held through the send: the parked call is still the ONLY one to have reached the wire.
        await Task.Delay(100);
        await Assert.That(probe.EnteredCount).IsEqualTo(1);
        await Assert.That(probe.CompletedInOrder.Count).IsEqualTo(0);

        probe.ReleaseFirstSend();
        await slowPeriodic.WaitAsync(TimeSpan.FromSeconds(5));
        await PollUntilAsync(() => probe.CompletedInOrder.Count >= 2);
        await Assert.That(probe.CompletedInOrder.Count).IsEqualTo(2);

        // (ii) completion (= wire) order carries non-decreasing content.
        var seqs = probe.CompletedInOrder
            .Select(r => r.LiveAgents.Single(a => a.Id == agent.Id).ActivitySeq)
            .ToList();
        await Assert.That(seqs[0]).IsEqualTo(1UL);
        await Assert.That(seqs[1]).IsEqualTo(2UL);
    }

    /// <summary>Contract pin for the "no correlation nonce" half of the design: the unsolicited
    /// delivery-triggered report reuses the ONE report shape, and that shape carries no nonce/echo
    /// member at all — so it is structurally incapable of masquerading as the answer to a correlated
    /// server-side status request. Should a correlated request/echo ever be added to this DTO, this
    /// test fails and forces the unsolicited emission to be re-examined (it must leave the field
    /// unset) rather than silently inheriting it.</summary>
    [Test]
    public async Task The_report_shape_carries_no_correlation_nonce() {
        var reportMembers = typeof(DaemonStatusReport).GetProperties().Select(p => p.Name);
        var agentMembers  = typeof(LiveAgentInfo).GetProperties().Select(p => p.Name);

        foreach (var name in reportMembers.Concat(agentMembers)) {
            await Assert.That(name.Contains("nonce", StringComparison.OrdinalIgnoreCase)).IsFalse();
            await Assert.That(name.Contains("echo", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }
    }

    /// <summary><see cref="ServerConnection"/> double that can PARK its first
    /// <see cref="ServerConnection.DaemonStatusReportAsync"/> call inside the send and records reports
    /// in COMPLETION order (not entry order) — completion order is what "wire order" means for the
    /// ordering section, and recording at entry would make a section-less implementation look correct.
    /// </summary>
    sealed class OrderingProbeServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        readonly Lock                     _gate      = new();
        readonly List<DaemonStatusReport> _completed = [];
        readonly TaskCompletionSource     _entered   = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource     _release   = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int                               _enteredCount;

        /// <summary>Completes the instant the first send is entered — the test drives its concurrent
        /// emission from exactly that window rather than from a timing guess.</summary>
        public Task FirstSendEntered => _entered.Task;

        public int EnteredCount => Volatile.Read(ref _enteredCount);

        public IReadOnlyList<DaemonStatusReport> CompletedInOrder {
            get { lock (_gate) return [.. _completed]; }
        }

        public void ReleaseFirstSend() => _release.TrySetResult();

        public override async Task DaemonStatusReportAsync(DaemonStatusReport report) {
            if (Interlocked.Increment(ref _enteredCount) == 1) {
                _entered.TrySetResult();
                await _release.Task;
            }

            lock (_gate) _completed.Add(report);
        }
    }
}
