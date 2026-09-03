namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

using Capacitor.Cli.Core.Policy;
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
