namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

using Harness = AcpLaunchNoticeHarness;

/// <summary>
/// A model the launch picked but the vendor drops is disclosed in the transcript; a model that was
/// applied, and a daemon-wide default the vendor drops, are not (a note on every launch would train
/// the user to ignore it).
/// </summary>
public class AcpModelFallbackNoticeTests {
    [Test]
    public async Task A_requested_model_the_vendor_does_not_publish_is_reported_as_a_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-mismatch", model: "gemini-3.7-flash"), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        var notes = Harness.SystemNotes(start.Transcript!).ToList();
        await Assert.That(notes.Count).IsEqualTo(1);
        await Assert.That(notes[0]).Contains("gemini-3.7-flash");
        await Assert.That(notes[0]).Contains("cursor");
    }

    /// The orchestrator clears the model for a vendor whose selector cannot apply one, so the pick
    /// never reaches the selector at all — the note has to come from what the launch asked for, not
    /// from what the handshake was handed.
    [Test]
    public async Task A_pick_the_orchestrator_cleared_is_still_reported() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-cleared", model: null, droppedPick: "gemini-3.7-flash"), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        var notes = Harness.SystemNotes(start.Transcript!).ToList();
        await Assert.That(notes.Count).IsEqualTo(1);
        await Assert.That(notes[0]).Contains("gemini-3.7-flash");
    }

    [Test]
    public async Task An_applied_model_emits_no_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-applied", model: "cursor-smart"), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        await Assert.That(start.Transcript!.ResolvedModel).IsEqualTo("cursor-smart");
        await Assert.That(Harness.SystemNotes(start.Transcript!)).IsEmpty();
    }

    /// A launch that picked nothing still carries the daemon-wide default down to the selector, and
    /// the models published here deliberately exclude it — so the drop really does happen and the
    /// silence is the gate doing its job, not the absence of anything to report.
    [Test]
    public async Task A_daemon_default_the_vendor_does_not_publish_is_dropped_silently() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(Harness.MakeContext("agent-default"), h.Cts.Token)
            .WaitAsync(Harness.HangGuard);

        await Assert.That(start.Transcript!.ResolvedModel).IsNull();
        await Assert.That(Harness.SystemNotes(start.Transcript!)).IsEmpty();
    }
}
