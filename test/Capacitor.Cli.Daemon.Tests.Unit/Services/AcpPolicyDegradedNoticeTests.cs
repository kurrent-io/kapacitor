namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Harness = AcpLaunchNoticeHarness;

/// <summary>
/// A hosted ACP session whose approval policy came up weakened says so in its own transcript, where
/// the person watching the session sees it — the daemon log carrying the same fact is not
/// user-visible, and a launch that runs under a policy the user believes is intact is exactly the
/// case the disclosure exists for.
/// </summary>
public class AcpPolicyDegradedNoticeTests {
    static PolicySnapshot Snapshot(bool degraded, params string[] degradations) =>
        new("snap-id", [], degraded, degradations);

    [Test]
    public async Task A_degraded_launch_snapshot_is_reported_as_a_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(
                Harness.MakeContext("agent-degraded",
                    policySnapshot: Snapshot(true, "user policy at /home/u/approvals.yaml ignored: 'version' must be 1")),
                h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        var notes = Harness.SystemNotes(start.Transcript!).ToList();
        await Assert.That(notes.Count).IsEqualTo(1);
        await Assert.That(notes[0]).IsEqualTo(
            "approval policy degraded: user policy at /home/u/approvals.yaml ignored: 'version' must be 1");
    }

    [Test]
    public async Task A_clean_launch_snapshot_emits_no_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-clean", policySnapshot: Snapshot(false)), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        await Assert.That(Harness.SystemNotes(start.Transcript!)).IsEmpty();
    }

    /// <summary>
    /// The read loop admits inbound peer frames — <c>session/request_permission</c> among them — from
    /// the instant it starts, so the disclosure has to be queued ahead of it: a peer acting under the
    /// weakened policy while the warning is still pending is exactly what the note exists to prevent.
    ///
    /// <para>An unsolicited <c>session/update</c> stands in for that permission request. Both are
    /// admitted by the same read loop, so its position relative to the note pins the same ordering;
    /// a permission request cannot substitute directly, because its arrival is only observable
    /// through the interaction bridge's answer rather than on the transcript's ordered lane. A
    /// <c>tool_call</c> update is used rather than a message chunk because chunk runs are held open
    /// for aggregation until a turn ends, which would make the envelope's position say nothing about
    /// when the frame arrived.</para>
    /// </summary>
    [Test]
    public async Task The_degraded_note_precedes_everything_the_read_loop_admits() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast");
        var heldInitialize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fake.HoldInitializeResponse = heldInitialize;
        h.StartFakeAgentLoop();

        var start = h.Factory.StartAsync(
            Harness.MakeContext("agent-early", policySnapshot: Snapshot(true, "user policy ignored")),
            h.Cts.Token);

        // The agent holds the initialize request, so the read loop is provably live and admitting.
        await h.Fake.InitializeReceived.WaitAsync(Harness.HangGuard);
        await h.Fake.WriteRawFrameAsync(
            FakeAcpAgent.BuildSessionUpdateNotification(
                FakeAcpAgent.FixedSessionId,
                FakeAcpAgent.BuildToolCallUpdate("call-1", "rm -rf /", "execute", "pending")),
            h.Cts.Token);

        heldInitialize.SetResult();
        var started = await start.WaitAsync(Harness.HangGuard);

        var envelopes = Harness.Drain(started.Transcript!);
        var noteAt    = envelopes.FindIndex(e => e.Kind == AcpEventKind.SystemNote);
        var toolCall  = envelopes.FindIndex(e => e.Kind == AcpEventKind.ToolCall);
        await Assert.That(noteAt).IsEqualTo(0);
        await Assert.That(toolCall).IsGreaterThan(noteAt);
    }

    /// <summary>An ungoverned launch carries no snapshot at all, and must read exactly like a clean
    /// one — a note here would fire on every launch on a machine with no policy files.</summary>
    [Test]
    public async Task An_ungoverned_launch_emits_no_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-ungoverned"), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        await Assert.That(Harness.SystemNotes(start.Transcript!)).IsEmpty();
    }
}
