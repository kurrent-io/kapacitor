using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="CodexSubagentTurnTracker"/> — the per-child turn-completion
/// state a Codex collab child watcher folds from its own rollout lines to decide when to
/// post the LIVE <c>/hooks/subagent-stop</c>. Before this, the only stop was the
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

    // ── BackfillCodexWatcherStateAsync (resume recovery) ──────────────────
    // Every Codex watcher resumes from the server watermark, so the drain never re-delivers
    // already-acknowledged lines — for a child, the task_complete that should drive the live
    // stop; for either role, a function_call whose output has not landed yet. The backfill
    // rebuilds both from the window of rollout lines just before that cursor.

    const string FunctionCall =
        """{"timestamp":"2026-08-11T08:55:00.000Z","type":"response_item","payload":{"type":"function_call","name":"shell","call_id":"call_seed1","arguments":"{}"}}""";

    const string FunctionCallOutput =
        """{"timestamp":"2026-08-11T08:55:05.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_seed1","output":"ok"}}""";

    static async Task<WatchState> BackfilledFrom(bool isChildWatcher, int? upToLine, params string[] lines) {
        var tmp = Directory.CreateTempSubdirectory("kcap-cstt").FullName;
        try {
            var path = Path.Combine(tmp, "rollout-2026-08-11T10-54-19-child.jsonl");
            await File.WriteAllLinesAsync(path, lines);

            var state = new WatchState();
            await WatchCommand.BackfillCodexWatcherStateAsync(
                state, path, isChildWatcher, upToLine ?? lines.Length, CancellationToken.None);

            return state;
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task Backfill_FileEndingWithTaskComplete_RearmsTheChildStop() {
        var state = await BackfilledFrom(isChildWatcher: true, upToLine: null, FunctionCall, FunctionCallOutput, TaskComplete);

        await Assert.That(state.CodexSubagentTurn.TurnCompleted).IsTrue();
        await Assert.That(state.PendingCodexToolCalls).IsEmpty();
        await Assert.That(Eligible(state.CodexSubagentTurn)).IsTrue();
    }

    [Test]
    public async Task Backfill_FileEndingMidTurn_StaysDisarmed() {
        // A completed earlier turn followed by a live tool call: not completed, call pending.
        var state = await BackfilledFrom(isChildWatcher: true, upToLine: null, TaskComplete, UserMessage, FunctionCall);

        await Assert.That(state.CodexSubagentTurn.TurnCompleted).IsFalse();
        await Assert.That(state.PendingCodexToolCalls).Contains("call_seed1");
        await Assert.That(Eligible(state.CodexSubagentTurn, pending: state.PendingCodexToolCalls.Count)).IsFalse();
    }

    // The gap this fixes: a session watcher (no agent id) that reconnects while a tool is
    // still running came up with an empty pending set, so ShouldEndOnIdle read toolInFlight
    // false and could end a live session with reason idle_timeout.
    [Test]
    public async Task Backfill_RecoversACallLeftOpenBeforeTheCursor_ForASessionWatcher() {
        var state = await BackfilledFrom(isChildWatcher: false, upToLine: null, FunctionCall, TokenCount);

        await Assert.That(state.PendingCodexToolCalls).Contains("call_seed1");
    }

    // The decision the backfill exists to protect, wired the way RunWatch wires it: without the
    // recovered call_id the set is empty, toolInFlight reads false, and a session whose tool is
    // still running is ended with reason idle_timeout.
    [Test]
    public async Task Backfill_KeepsASessionWithARunningTool_OffTheIdleEnd() {
        var state = await BackfilledFrom(isChildWatcher: false, upToLine: null, FunctionCall, TokenCount);

        var idle = WatchCommand.ShouldEndOnIdle(
            "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: T0, now: T0 + TimeSpan.FromHours(3),
            idleTimeout: WatchCommand.DefaultCodexIdleTimeout,
            toolInFlight: state.PendingCodexToolCalls.Count > 0);

        await Assert.That(idle).IsFalse();
    }

    // Turn state only drives the child's live subagent-stop; a session watcher never reads it,
    // so folding it there would be state kept for nobody.
    [Test]
    public async Task Backfill_DoesNotFoldTurnState_ForASessionWatcher() {
        var state = await BackfilledFrom(isChildWatcher: false, upToLine: null, TaskComplete);

        await Assert.That(state.CodexSubagentTurn.TurnCompleted).IsFalse();
    }

    // Only the prefix is the watcher's blind spot; the cursor onwards arrives through the
    // normal drain, which folds it there.
    [Test]
    public async Task Backfill_StopsAtTheCursor() {
        var state = await BackfilledFrom(isChildWatcher: true, upToLine: 1, FunctionCall, FunctionCallOutput, TaskComplete);

        await Assert.That(state.PendingCodexToolCalls).Contains("call_seed1");
        await Assert.That(state.CodexSubagentTurn.TurnCompleted).IsFalse();
    }

    // Bounded like the Claude backfill: an in-flight call sits at the tail by construction —
    // nothing is written between a function_call and its output — so anything further back is
    // settled, and a window keeps startup work off the length of the rollout.
    [Test]
    public async Task Backfill_IgnoresACallOlderThanTheScanWindow() {
        var lines = new List<string> { FunctionCall };  // never resolved, then buried
        lines.AddRange(Enumerable.Repeat(TokenCount, WatchCommand.ToolBackfillWindowLines + 10));

        var state = await BackfilledFrom(isChildWatcher: true, upToLine: null, [.. lines]);

        await Assert.That(state.PendingCodexToolCalls).IsEmpty();
    }

    [Test]
    public async Task Backfill_MissingFile_IsANoOp() {
        var state = new WatchState();

        await WatchCommand.BackfillCodexWatcherStateAsync(
            state, "/nonexistent/rollout.jsonl", isChildWatcher: true, upToLine: 5, CancellationToken.None);

        await Assert.That(state.CodexSubagentTurn.TurnCompleted).IsFalse();
        await Assert.That(state.PendingCodexToolCalls).IsEmpty();
    }

    [Test]
    public async Task Backfill_ReturnsSilentlyWhenAlreadyCancelled() {
        var tmp = Directory.CreateTempSubdirectory("kcap-cstt").FullName;
        try {
            var path = Path.Combine(tmp, "rollout.jsonl");
            await File.WriteAllLinesAsync(path, [FunctionCall]);

            var       state     = new WatchState();
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await WatchCommand.BackfillCodexWatcherStateAsync(
                state, path, isChildWatcher: true, upToLine: 1, cancelled.Token);

            await Assert.That(state.PendingCodexToolCalls).IsEmpty();
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
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
