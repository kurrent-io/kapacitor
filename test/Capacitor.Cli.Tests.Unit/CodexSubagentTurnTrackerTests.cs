using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="CodexSubagentTurnTracker"/> — the per-child turn-completion
/// state a Codex collab child watcher folds from its own rollout lines to decide when to
/// post the LIVE <c>/hooks/subagent-stop</c> (AI-1861). Before this, the only stop was the
/// parent's session-end teardown, so every finished child card spun for the parent's whole
/// lifetime. The lifecycle is one-shot server-side (deterministic event ids), so the
/// tracker latches after one successful post and never re-arms.
/// </summary>
public class CodexSubagentTurnTrackerTests {
    static readonly DateTimeOffset T0    = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan       Grace = TimeSpan.FromMinutes(5);

    // Real 0.148 rollout shapes (trimmed to the fields the tracker reads).
    const string TaskComplete =
        """{"timestamp":"2026-08-11T08:58:33.027Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1","last_agent_message":"done","duration_ms":253118}}""";

    const string TaskStarted =
        """{"timestamp":"2026-08-11T08:59:00.000Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t2"}}""";

    const string TokenCount =
        """{"timestamp":"2026-08-11T08:58:32.817Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1}}}}""";

    const string UserMessage =
        """{"timestamp":"2026-08-11T09:00:16.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"follow-up"}]}}""";

    static bool Eligible(CodexSubagentTurnTracker tracker, int pending = 0, TimeSpan? sinceActivity = null) =>
        tracker.ShouldPostStop(pending, lastActivityAt: T0, now: T0 + (sinceActivity ?? Grace), grace: Grace);

    [Test]
    public async Task FreshTracker_IsNotStopEligible() {
        var tracker = new CodexSubagentTurnTracker();

        await Assert.That(Eligible(tracker)).IsFalse();
    }

    [Test]
    public async Task TaskComplete_BecomesEligible_OnceGraceElapsed() {
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);

        await Assert.That(Eligible(tracker, sinceActivity: Grace - TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(Eligible(tracker)).IsTrue();
    }

    [Test]
    public async Task ZeroGrace_IsEligible_ImmediatelyAtTaskComplete() {
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);

        await Assert.That(tracker.ShouldPostStop(0, lastActivityAt: T0, now: T0, grace: TimeSpan.Zero)).IsTrue();
    }

    [Test]
    public async Task PendingToolCall_SuppressesTheStop() {
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);

        await Assert.That(Eligible(tracker, pending: 1)).IsFalse();
    }

    [Test]
    public async Task ResponseItemAfterTaskComplete_ClearsCompletion() {
        // Re-engagement: the parent send_message lands a new response_item in the child
        // rollout — the child is working again, the stop must not fire.
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);
        tracker.Observe(UserMessage);

        await Assert.That(Eligible(tracker)).IsFalse();
    }

    [Test]
    public async Task TaskStartedAfterTaskComplete_ClearsCompletion() {
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);
        tracker.Observe(TaskStarted);

        await Assert.That(Eligible(tracker)).IsFalse();
    }

    [Test]
    public async Task TrailingEventMsgNoise_DoesNotClearCompletion() {
        // Codex can emit further event_msg lines (token_count et al.) after task_complete;
        // only real turn activity (response_item / task_started) re-opens the turn.
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);
        tracker.Observe(TokenCount);

        await Assert.That(Eligible(tracker)).IsTrue();
    }

    [Test]
    public async Task StopPosted_Latches_EvenAcrossALaterTaskComplete() {
        // The server-side lifecycle is one-shot per (session, agent): a second stop would
        // dedupe to the same deterministic event id, so the tracker never re-arms.
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);
        tracker.StopPosted = true;

        await Assert.That(Eligible(tracker)).IsFalse();

        tracker.Observe(UserMessage);
        tracker.Observe(TaskComplete);

        await Assert.That(Eligible(tracker)).IsFalse();
    }

    [Test]
    public async Task MalformedAndForeignLines_AreIgnored() {
        var tracker = new CodexSubagentTurnTracker();
        tracker.Observe(TaskComplete);
        tracker.Observe("not json at all");
        tracker.Observe("""{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""");

        await Assert.That(Eligible(tracker)).IsTrue();
    }

    // ── ResolveStopGrace ──────────────────────────────────────────────────

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("garbage")]
    [Arguments("-1")]
    public async Task ResolveStopGrace_FallsBackToDefault(string? envValue) {
        await Assert.That(CodexSubagentTurnTracker.ResolveStopGrace(envValue))
                    .IsEqualTo(CodexSubagentTurnTracker.DefaultStopGrace);
    }

    [Test]
    public async Task ResolveStopGrace_ZeroMeansImmediate() {
        await Assert.That(CodexSubagentTurnTracker.ResolveStopGrace("0")).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task ResolveStopGrace_ParsesMinutes() {
        await Assert.That(CodexSubagentTurnTracker.ResolveStopGrace("3")).IsEqualTo(TimeSpan.FromMinutes(3));
    }
}
