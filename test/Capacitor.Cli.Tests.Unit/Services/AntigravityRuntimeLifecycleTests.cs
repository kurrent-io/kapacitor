// test/Capacitor.Cli.Tests.Unit/Services/AntigravityRuntimeLifecycleTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Tests.Unit.Services.AntigravityRuntimeFakes;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Exercises <see cref="AntigravityHostedAgentRuntime"/>'s phase machine end-to-end against
/// <see cref="FakeAgyTurnProcess"/> (in <c>AntigravityRuntimeFakes.cs</c>, shared with a sibling plan)
/// — no real <c>agy</c> process is spawned. This is the highest-risk runtime in the daemon: unlike
/// every other <c>IHostedAgentRuntime</c>, it has no long-lived child at all — each turn is its own
/// <c>agy -p …</c> invocation, so "the process exited" and "the runtime is done" are NOT the same
/// event, and getting that distinction wrong produces a silent hang rather than a failing test. See
/// the class doc on <see cref="AntigravityHostedAgentRuntime"/> for the rules these tests pin.
///
/// <para><b>Why most tests await <see cref="AntigravityHostedAgentRuntime.WaitForConversationIdAsync"/>
/// before <see cref="AntigravityHostedAgentRuntime.WaitForTurnIdleAsync"/>.</b>
/// <see cref="AntigravityHostedAgentRuntime.SendUserInputAsync"/> returns as soon as a turn is
/// enqueued — before the worker even dequeues it — and <c>WaitForTurnIdleAsync</c>'s
/// acquire-then-release on a currently-FREE gate can complete synchronously, before the worker's own
/// continuation is even scheduled to acquire it. Calling <c>WaitForTurnIdleAsync</c> right after
/// <c>SendUserInputAsync</c> is therefore not a reliable "the turn ran" barrier — a review caught this
/// after it let a comparison test pass on two empty strings without a single turn actually completing.
/// <c>WaitForConversationIdAsync</c> only resolves once the worker has genuinely read a spawned turn's
/// first line, so awaiting it first closes the gap.</para>
/// </summary>
public class AntigravityRuntimeLifecycleTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Between_turns_the_runtime_is_logically_alive() {
        await using var rt = FakeRuntime();
        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Positive check that the turn genuinely ran (not "" == "" from a turn that never started).
        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);

        // No child process exists right now. Reporting exited here would make every stop
        // trivially succeed and would mis-report the final status.
        await Assert.That(rt.HasExited).IsFalse();
        await Assert.That(rt.ExitCode).IsNull();
    }

    [Test]
    public async Task ReadOutputAsync_does_not_complete_while_idle() {
        await using var rt = FakeRuntime();
        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);

        // Completing here would drive FinalizeAgentRunAsync and close the session after turn 1.
        await Assert.That(read.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ReadOutputAsync_completes_once_terminal_is_entered() {
        await using var rt = FakeRuntime();
        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });

        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(HangGuard);
        await read.WaitAsync(HangGuard);

        await Assert.That(read.IsCompletedSuccessfully).IsTrue();
        await Assert.That(rt.HasExited).IsTrue();
    }

    [Test]
    public async Task Eof_without_a_terminal_result_drives_terminal_not_idle() {
        await using var rt = FakeRuntime(turn: FakeTurn.EofWithoutResult);
        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

        // A turn child that died without a terminal `result` has lost the reviewer.
        // Going Idle here would park ReadOutputAsync forever.
        await read.WaitAsync(HangGuard);
        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(rt.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task Terminate_while_idle_is_not_a_no_op() {
        await using var rt = FakeRuntime();
        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);

        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsTrue();
    }

    [Test]
    public async Task Terminate_during_a_long_turn_completes_rather_than_deadlocking() {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds, onSpawn: _ => spawned.TrySetResult());
        _ = rt.SendUserInputAsync("hello");

        // Structural, not timing-based: proves the NeverEnds turn has genuinely spawned (and so is
        // genuinely holding _turnGate inside its read loop) before TerminateAsync races it — the
        // mutation check pins that TerminateAsync would deadlock here if it took the gate itself.
        await spawned.Task.WaitAsync(HangGuard);

        // If TerminateAsync took the turn gate, this would hang forever: the turn holds the
        // gate, Terminal could never be entered, and the park would never resolve.
        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(rt.HasExited).IsTrue();
    }

    [Test]
    public async Task Terminate_racing_a_still_running_spawn_reaps_the_process_it_never_got_to_track() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAgyTurnProcess? spawnedProcess = null;

        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = async (_, _, _) => {
            entered.TrySetResult();

            // Deliberately ignores the passed-in token: TerminateAsync (below) cancels _ownerCts
            // before this returns, and the spawn must still SUCCEED afterward — otherwise
            // SpawnTurnProcessAsync's own "stop/dispose raced the spawn" branch would fire instead,
            // and this test would exercise a different path than the one under test.
            await release.Task.ConfigureAwait(false);

            var process = new FakeAgyTurnProcess(FakeTurn.Normal, FixedConversationId);
            spawnedProcess = process;
            return process;
        };

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

        // The worker is now parked INSIDE _spawnTurn — before ProcessTurnAsync has any process to
        // publish to _current at all.
        await entered.Task.WaitAsync(HangGuard);

        // Guaranteed to win the "publish vs. capture" race under _stateLock: ProcessTurnAsync's
        // publish literally cannot run until the spawn call above returns, which it hasn't yet.
        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(HangGuard);

        // NOW let the spawn actually complete — the process is only handed back to ProcessTurnAsync
        // once the runtime is ALREADY Terminal, exercising its atomic already-Terminal check.
        release.TrySetResult();

        var deadline = DateTime.UtcNow + HangGuard;
        while (spawnedProcess is not { DisposeCalls: >= 1 } && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(spawnedProcess).IsNotNull();
        // Reaped (never orphaned) AND disposed (never leaked) despite never being tracked in _current.
        await Assert.That(spawnedProcess!.TerminateCalls).IsEqualTo(1);
        await Assert.That(spawnedProcess.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_turn_enqueued_after_terminal_is_dropped_without_spawning() {
        var spawns = 0;
        await using var rt = FakeRuntime(onSpawn: _ => spawns++);
        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(HangGuard);

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await Task.Delay(100);

        await Assert.That(spawns).IsEqualTo(0);
    }

    [Test]
    public async Task The_conversation_id_is_stable_and_a_change_drives_terminal() {
        // WaitForConversationIdAsync only ever resolves ONCE (turn 1's id) — it can't also prove turn
        // 2 genuinely ran. Track each spawn explicitly instead of relying on WaitForTurnIdleAsync's
        // racy gate hand-off (see the class doc).
        var spawnSignals = new TaskCompletionSource[2];
        for (var i = 0; i < spawnSignals.Length; i++)
            spawnSignals[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnCount = 0;

        await using var rt = FakeRuntime(onSpawn: _ => {
            var i = Interlocked.Increment(ref spawnCount) - 1;
            if (i < spawnSignals.Length) spawnSignals[i].TrySetResult();
        });

        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);
        await spawnSignals[0].Task.WaitAsync(HangGuard); // turn 1 genuinely started
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);
        var first = rt.AcpSessionId;

        // Positive check — a vacuous run (turn 1 never actually processed) would leave this "".
        await Assert.That(first).IsEqualTo(FixedConversationId);

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);
        await spawnSignals[1].Task.WaitAsync(HangGuard); // turn 2 genuinely started
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // A changed conversation id means we silently forked the reviewer's history.
        await Assert.That(rt.AcpSessionId).IsEqualTo(first);
        await Assert.That(rt.HasExited).IsFalse(); // still idle — two clean turns, no mismatch.
    }

    [Test]
    public async Task A_changed_conversation_id_mid_session_drives_terminal() {
        // FakeRuntime always uses ONE FakeTurn for every spawn, so a mismatch (which only makes sense
        // after a first turn has already established an id) needs a bespoke spawn closure: turn 1
        // Normal (establishes the baseline id), turn 2 ChangedConversationId (reports a different one).
        var callIndex = 0;
        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (_, _, _) => {
            var kind = Interlocked.Increment(ref callIndex) == 1 ? FakeTurn.Normal : FakeTurn.ChangedConversationId;
            return Task.FromResult<IAgyTurnProcess>(new FakeAgyTurnProcess(kind, FixedConversationId));
        };

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);

        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);
        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);
        await Assert.That(rt.HasExited).IsFalse(); // idle after a clean first turn, not terminal yet.

        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });
        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);

        // The mismatch drives Terminal, never Idle — wait on the terminal signal itself, since this
        // turn will never reach WaitForTurnIdleAsync's "idle" outcome.
        await read.WaitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(rt.ExitCode).IsNotEqualTo(0);

        // The stable id from turn 1 is preserved — never silently overwritten by the mismatched one.
        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);
    }

    [Test]
    public async Task Spawner_receives_the_prompt_text() {
        var prompts = new List<string>();
        await using var rt = FakeRuntime(onSpawn: p => prompts.Add(p));

        await rt.SendUserInputAsync("do the review").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(prompts).Contains("do the review");
    }

    // ── the bounded input queue ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A turn is one process, so input that arrives mid-turn cannot steer the running turn. It must
    /// become the NEXT turn — in order, and as a separate turn per message: concatenating two of a
    /// user's messages into one prompt changes their meaning, and a turn's prompt is its process's
    /// argument, so a coalesced pair is unrecoverable rather than merely untidy.
    ///
    /// <para>Deterministic by construction rather than by delay: turn 1 is a
    /// <see cref="GatedTurnProcess"/> that has emitted its <c>init</c> (so
    /// <c>WaitForConversationIdAsync</c> proves it is genuinely mid-turn, holding the gate) and does
    /// not finish until this test releases it — so the two later sends are guaranteed to be enqueued
    /// while a turn is in flight, which is the whole point.</para>
    /// </summary>
    [Test]
    public async Task Input_sent_during_a_turn_becomes_the_next_turn_in_order() {
        var prompts = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawns  = 0;

        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (prompt, _, _) => {
            lock (prompts) prompts.Add(prompt);

            // Only turn 1 is held open; later turns run to completion normally.
            return Task.FromResult<IAgyTurnProcess>(Interlocked.Increment(ref spawns) == 1
                ? new GatedTurnProcess(release.Task, FixedConversationId)
                : new FakeAgyTurnProcess(FakeTurn.Normal, FixedConversationId));
        };

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);

        await rt.SendUserInputAsync("first").WaitAsync(HangGuard);

        // Turn 1 has read its own init line — it is genuinely executing and holding the turn gate.
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rt.SendUserInputAsync("second").WaitAsync(HangGuard);
        await rt.SendUserInputAsync("third").WaitAsync(HangGuard);

        release.TrySetResult();

        var deadline = DateTime.UtcNow + HangGuard;
        while (Volatile.Read(ref spawns) < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);

        string[] snapshot;
        lock (prompts) snapshot = [.. prompts];

        // Joined rather than compared element-wise so ONE assertion pins both halves: a reordered
        // queue and a coalesced pair (which would arrive as a single prompt containing all three
        // texts) each produce a different string.
        await Assert.That(string.Join("|", snapshot)).IsEqualTo("first|second|third");
    }

    /// <summary>
    /// Over-cap input is <b>rejected visibly</b>, never silently discarded. The two alternatives are
    /// both wrong in their own way: blocking the caller would stall the daemon's command lane behind
    /// a stalled reviewer's turn (the queue is only ever full BECAUSE a turn is stuck), and dropping
    /// silently loses a message the user has already sent with nothing to tell them so. Both send
    /// entry points reject — the acknowledging one (used for borrowed launches) and the plain one
    /// (every server-driven <c>SendInput</c>), since a caller that never asked for a write ack is
    /// exactly the caller that would otherwise never learn.
    /// </summary>
    [Test]
    public async Task An_over_cap_enqueue_is_rejected_visibly_rather_than_dropped() {
        var spawns = 0;
        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds, onSpawn: _ => spawns++, queueCap: 1);

        // Turn 1 is picked up immediately and never ends, holding the gate — turn 2 fills the
        // 1-deep queue, and a 3rd send has nowhere to go.
        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (Volatile.Read(ref spawns) < 1 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Assert.That(spawns).IsEqualTo(1);

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAsync("three").WaitAsync(HangGuard));
        await Assert.That(rejected!.Message).Contains("queue is full");

        // The acknowledging entry point rejects identically — same queue, same answer.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAndWaitForWriteAsync("four").WaitAsync(HangGuard));

        await Task.Delay(100);

        // Only turn 1 ever spawned — turn 2 sits in the (capacity-1) queue, and neither rejected
        // message became a turn.
        await Assert.That(spawns).IsEqualTo(1);
    }

    /// <summary>
    /// The rejection reaches the HUMAN, not just the daemon log: a faulted send task is observed by
    /// the orchestrator's own handler, which logs it and returns — the user's dashboard would show
    /// nothing at all. A <c>system_note</c> envelope on the runtime's own transcript is the one
    /// surface the person who typed the message actually sees.
    ///
    /// <para>One note PER rejected message, deliberately not one per full-queue episode: each
    /// rejected message is a separately lost message, and telling the user about the first while
    /// swallowing the rest is the silent drop this whole rule exists to prevent. The transcript
    /// channel is DropOldest-bounded, so a pathological sender cannot grow memory here.</para>
    /// </summary>
    [Test]
    public async Task A_rejected_over_cap_enqueue_tells_the_user_on_the_transcript() {
        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds, queueCap: 1);

        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);

        // Turn 1 is genuinely executing (its init has been read), so the queue state below is real.
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAsync("three").WaitAsync(HangGuard));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAsync("four").WaitAsync(HangGuard));

        var envelopes = new List<AcpEventEnvelope>();
        while (rt.Envelopes.TryRead(out var env)) envelopes.Add(env);

        var notes = envelopes.Where(e => e.Kind == AcpEventKind.SystemNote).ToList();

        // Two rejected messages, two notes — never one summary note for both, and never a note for the
        // turn that WAS queued (three sends, two of them rejected, would otherwise read as three).
        await Assert.That(notes.Count).IsEqualTo(2);
        await Assert.That(notes[0].Text).IsNotNull();
        await Assert.That(notes[0].Text!).Contains("not delivered");
    }

    /// <summary>
    /// <b>A REJECTED input must not refresh the liveness attestation.</b> The rejection note goes onto
    /// the transcript through the same emit path as agent output, whose contract is to advance the
    /// activity clock ("the content was genuinely produced"). That justification does not hold for a
    /// note the DAEMON authored about input it refused: the queue is full only because a turn is
    /// wedged, so every retry against a stuck agent would reset <c>IdleForMs</c> and bump
    /// <c>ActivitySeq</c> — the user's attempts to unstick it keeping the supervisor from ever seeing
    /// it as idle. Bounded for a reviewer (its TTL arm fires regardless), unbounded for a hosted agent.
    ///
    /// <para>Both attestation fields are asserted, because they fail independently: <c>ActivitySeq</c>
    /// is what a server compares between reports, <c>IdleForMs</c> is what a reaper thresholds on.</para>
    /// </summary>
    [Test]
    public async Task A_rejected_enqueue_does_not_advance_the_activity_clock() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds, queueCap: 1);

        rt.ActivityClock = clock;

        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);

        // Turn 1 is genuinely executing (its init has been read), so the queue state below is real —
        // and the launch's own stamps have already landed before the readings are taken.
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);

        // Non-zero idleness to lose: without this the "unchanged" assertion below would hold trivially
        // at 0 whether or not the clock advanced.
        time.Advance(TimeSpan.FromSeconds(7));

        var seqBefore  = clock.ActivitySeq;
        var idleBefore = clock.IdleForMs;

        await Assert.That(idleBefore).IsEqualTo(7000UL);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAsync("three").WaitAsync(HangGuard));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.SendUserInputAsync("four").WaitAsync(HangGuard));

        // The notes DID reach the transcript — this is about the clock, not about suppressing them.
        var notes = 0;
        while (rt.Envelopes.TryRead(out var env))
            if (env.Kind == AcpEventKind.SystemNote) notes++;

        await Assert.That(notes).IsEqualTo(2);

        await Assert.That(clock.ActivitySeq).IsEqualTo(seqBefore);
        await Assert.That(clock.IdleForMs).IsEqualTo(idleBefore);
    }

    /// <summary>A turn child that emits its <c>init</c> and then holds the turn open until the test
    /// releases it, ending cleanly with a terminal <c>result</c>. The structural stand-in for "a turn
    /// is genuinely in flight" — unlike <see cref="FakeTurn.NeverEnds"/>, it can also be let go, so a
    /// test can observe what the queue does with the turns that piled up behind it.</summary>
    sealed class GatedTurnProcess(Task gate, string conversationId) : IAgyTurnProcess {
        public int  Pid       => 4251;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield return $$$"""{"event":"init","conversation_id":"{{{conversationId}}}","init":{"cwd":"/w"}}""";

            await gate.WaitAsync(ct).ConfigureAwait(false);

            yield return $$$"""{"event":"result","result":{"conversation_id":"{{{conversationId}}}","status":"SUCCESS"}}""";

            HasExited = true;
            ExitCode  = 0;
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode ??= -1;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task A_turns_process_is_disposed_after_the_turn_ends() {
        FakeAgyTurnProcess? spawned = null;
        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (_, _, _) => {
            spawned = new FakeAgyTurnProcess(FakeTurn.Normal, FixedConversationId);
            return Task.FromResult<IAgyTurnProcess>(spawned);
        };

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(spawned).IsNotNull();
        await Assert.That(spawned!.DisposeCalls).IsEqualTo(1);
    }

    // ── the per-turn durable PID record ───────────────────────────────────────────────────────────

    /// <summary>
    /// Exec-per-turn means every ROUND of a review is a different child with a different pid, so the
    /// one-shot record the orchestrator takes at launch names turn 1's and nothing after it. A daemon
    /// SIGKILL during round 2 would then leave a child reapable by neither the record pass nor the
    /// env-marker pass (which is gated on Linux, while this reviewer is POSIX-meaning-macOS in
    /// practice). Both directions are asserted: a record per spawn, DISTINCT per turn, and a clear on
    /// each confirmed exit — a record that is written but never cleared names a dead pid for the rest
    /// of the session.
    /// </summary>
    [Test]
    public async Task Each_turn_records_its_own_child_pid_and_clears_it_on_confirmed_exit() {
        var recorded = new List<int>();
        var cleared  = 0;
        var spawns   = 0;

        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (_, _, _) =>
            Task.FromResult<IAgyTurnProcess>(new FakeAgyTurnProcess(
                FakeTurn.Normal, FixedConversationId, pid: 5000 + Interlocked.Increment(ref spawns)));

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);
        rt.PidCallbacks = new AgyPidRecordCallbacks(
            Record: pid => { lock (recorded) recorded.Add(pid); },
            Clear:  () => Interlocked.Increment(ref cleared));

        // The write ack resolves once the turn's process has genuinely spawned, which is strictly
        // after the record — so two acks mean two records were attempted, with no polling for the
        // record half at all.
        await rt.SendUserInputAndWaitForWriteAsync("round 1").WaitAsync(HangGuard);
        await rt.SendUserInputAndWaitForWriteAsync("round 2").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (Volatile.Read(ref cleared) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);

        int[] snapshot;
        lock (recorded) snapshot = [.. recorded];

        // Distinct, not merely two: a runtime that re-recorded turn 1's pid would satisfy a count.
        await Assert.That(snapshot).IsEquivalentTo(new[] { 5001, 5002 });
        await Assert.That(cleared).IsEqualTo(2);
    }

    /// <summary>
    /// The fail-closed half of the contract: the record write throws by design, and a spawned child
    /// the daemon cannot durably record must not proceed. The turn fails, the child is reaped rather
    /// than left running untracked, and the runtime goes terminal — never swallowed into a turn that
    /// looks healthy.
    /// </summary>
    [Test]
    public async Task A_turn_whose_pid_record_is_refused_reaps_its_child_and_goes_terminal() {
        var process = new FakeAgyTurnProcess(FakeTurn.Normal, FixedConversationId);

        await using var rt = new AntigravityHostedAgentRuntime(
            spawnTurn: (_, _, _) => Task.FromResult<IAgyTurnProcess>(process),
            logger: NullLogger.Instance);

        rt.PidCallbacks = new AgyPidRecordCallbacks(
            Record: _ => throw new InvalidOperationException("pid record store is unavailable"),
            Clear:  () => { });

        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

        // Completes only if Terminal was entered — the turn was not quietly skipped.
        await read.WaitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(rt.ExitCode).IsNotEqualTo(0);

        // The untracked child was reaped, not abandoned.
        await Assert.That(process.TerminateCalls).IsEqualTo(1);
    }

    // ── the disposal callback's confirmed-exit gate ───────────────────────────────────────────────

    /// <summary>
    /// The callback removes the per-launch <c>HOME</c>, which holds the reviewer's own conversation
    /// JSONL — the caller's diff, source excerpts and findings. Every bound on the disposal path
    /// (<c>TerminateAsync</c>'s timeout, the turn worker's join, the process handle's own disposal) is
    /// best-effort and swallowed, so "nothing of ours can still be writing" has to be MEASURED rather
    /// than asserted: a <c>WaitAsync</c> that timed out is otherwise indistinguishable from one that
    /// succeeded. <c>Kill(entireProcessTree: true)</c> is not atomic against a grandchild forked
    /// between tree enumeration and signal — and agy's children include its MCP stdio servers — so a
    /// survivor is a real shape.
    ///
    /// <para>Unconfirmed means SKIP the deletion, not force it: deleting under a live reviewer would
    /// leave it writing into an unlinked path and recreating the directory, which is worse than
    /// leaving it. The epoch-keyed startup sweep collects it on the next boot. The skip must be
    /// LOGGED — a retained transcript-bearing home must never be silent.</para>
    /// </summary>
    [Test]
    public async Task Dispose_skips_the_teardown_callback_when_a_turn_child_never_confirmed_exit() {
        var invoked = 0;
        var logger  = new CaptureLogger();
        var process = new UnconfirmedExitTurnProcess();

        await using (var rt = new AntigravityHostedAgentRuntime(
                spawnTurn: (_, _, _) => Task.FromResult<IAgyTurnProcess>(process),
                logger: logger,
                agentId: "agy-survivor",
                onDisposed: () => Interlocked.Increment(ref invoked))) {
            await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

            // The turn genuinely spawned a child, so "unconfirmed" is a fact about a real process
            // rather than a runtime that never started one.
            await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        }

        await Assert.That(invoked).IsEqualTo(0);
        await Assert.That(logger.Entries).Contains(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("agy-survivor"));
    }

    /// <summary>The positive control for the gate above — without it a callback that never ran at all
    /// would satisfy the skip test.</summary>
    [Test]
    public async Task Dispose_runs_the_teardown_callback_once_the_turn_child_confirmed_exit() {
        var invoked = 0;

        await using (var rt = new AntigravityHostedAgentRuntime(
                spawnTurn: (_, _, _) => Task.FromResult<IAgyTurnProcess>(
                    new FakeAgyTurnProcess(FakeTurn.Normal, FixedConversationId)),
                logger: NullLogger.Instance,
                agentId: "agy-clean",
                onDisposed: () => Interlocked.Increment(ref invoked))) {
            await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
            await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
            await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

            await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);
        }

        await Assert.That(invoked).IsEqualTo(1);
    }

    /// <summary>
    /// The other half of the gate, and the one that was wrong: a child that LINGERS past its stdout
    /// EOF and the exit-confirmation grace, but dies the moment it is actually killed. The runtime's
    /// own turn teardown kills it — so it genuinely exited before the runtime let go of the handle —
    /// and the confirmed-exit consequences must therefore BOTH follow: the durable PID record is
    /// cleared, and the per-launch HOME (this callback) is removed.
    ///
    /// <para>Before the fix, <c>_turnExitConfirmed</c> was latched from <c>HasExited</c> BEFORE the
    /// kill that made it true, so this shape reported "unconfirmed": the fail-safe fired on a child
    /// that had in fact exited, stranding the reviewer's conversation JSONL on disk until the next
    /// daemon-epoch sweep and leaving a PID record naming a dead process. The fail-safe direction is
    /// unchanged — see the sibling test above, where the kill does NOT work and the skip still
    /// stands — only the moment the question is asked.</para>
    /// </summary>
    [Test]
    public async Task Dispose_runs_the_teardown_callback_when_a_lingering_turn_child_is_killed_at_teardown() {
        var invoked = 0;
        var cleared = 0;
        var process = new LingeringTurnProcess();

        await using (var rt = new AntigravityHostedAgentRuntime(
                spawnTurn: (_, _, _) => Task.FromResult<IAgyTurnProcess>(process),
                logger: NullLogger.Instance,
                agentId: "agy-lingerer",
                onDisposed: () => Interlocked.Increment(ref invoked))) {
            rt.PidCallbacks = new AgyPidRecordCallbacks(
                Record: _ => { },
                Clear:  () => Interlocked.Increment(ref cleared));

            await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
            await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
            await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

            // The child was genuinely still alive when its stdout hit EOF — otherwise this asserts
            // nothing about a lingering child at all, and the fake could have exited on its own.
            await Assert.That(process.WasKilledWhileRunning).IsTrue();
        }

        await Assert.That(cleared).IsEqualTo(1);
        await Assert.That(invoked).IsEqualTo(1);
    }

    /// <summary>A turn child whose stdout EOFs while the process itself is still running (it never
    /// exits on its own, and its bounded exit-confirmation wait times out), but which dies as soon as
    /// it is signalled — the ordinary shape of a child that outlives its own output by a moment.
    /// Terminate and dispose both kill, mirroring <c>AgyTurnProcess</c>, where both are the same
    /// <c>Kill(entireProcessTree: true)</c>.</summary>
    sealed class LingeringTurnProcess : IAgyTurnProcess {
        int _killed;

        public int  Pid       => 4250;
        public bool HasExited => Volatile.Read(ref _killed) != 0;
        public int? ExitCode  => HasExited ? 0 : null;

        /// <summary>True once a kill landed on a process that was still running — the precondition
        /// this fake exists to create, asserted rather than assumed.</summary>
        public bool WasKilledWhileRunning { get; private set; }

#pragma warning disable CS1998 // an async iterator whose lines are already in hand still needs the modifier
        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield return $$$"""{"event":"init","conversation_id":"{{{FixedConversationId}}}","init":{"cwd":"/w"}}""";
            yield return $$$"""{"event":"result","result":{"conversation_id":"{{{FixedConversationId}}}","status":"SUCCESS"}}""";

            // EOF here, with the process still running — HasExited stays false.
        }
#pragma warning restore CS1998

        /// <summary>Returns silently on timeout, per the interface contract — this child never exits
        /// of its own accord, so the runtime's grace wait always expires.</summary>
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) { Kill(); return Task.CompletedTask; }
        public ValueTask DisposeAsync()                      { Kill(); return ValueTask.CompletedTask; }

        void Kill() {
            if (Interlocked.Exchange(ref _killed, 1) == 0) WasKilledWhileRunning = true;
        }
    }

    /// <summary>A turn child that honours cancellation — so the turn worker unwinds normally — but
    /// never reports itself exited, and whose terminate does not make it so. The measured shape is a
    /// grandchild that outlived a non-atomic process-tree kill; the runtime cannot tell that apart
    /// from a child that simply has not died yet, and must not.</summary>
    sealed class UnconfirmedExitTurnProcess : IAgyTurnProcess {
        public int  Pid       => 4249;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield return $$$"""{"event":"init","conversation_id":"{{{FixedConversationId}}}","init":{"cwd":"/w"}}""";

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null)   => Task.CompletedTask;
        public ValueTask DisposeAsync()                        => ValueTask.CompletedTask;
    }

    /// <summary>Records every log call — mirrors the pattern the ACP suites already use.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(
                LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter) {
            lock (Entries) Entries.Add((level, formatter(state, ex)));
        }
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> that is NOT the owner's shutdown must still reach
    /// <c>EnterTerminal</c>. <c>ProcessTurnAsync</c> individually catches every
    /// <see cref="IAgyTurnProcess"/> call except its bounded exit-confirmation wait, and that method's
    /// contract ("returns silently on timeout") does not FORBID an implementation propagating its own
    /// internal cancellation — this runtime is built against the interface, not against
    /// <c>AgyTurnProcess</c>. A turn worker that read such an exception as "normal shutdown" would exit
    /// its loop without entering Terminal: <c>_terminalTcs</c> never completes,
    /// <see cref="AntigravityHostedAgentRuntime.ReadOutputAsync"/> parks forever, and the
    /// orchestrator's <c>FinalizeAgentRunAsync</c> never fires — rule (a)'s hang, reached through the
    /// one door rule (a) does not cover.
    /// </summary>
    [Test]
    public async Task An_unexpected_cancellation_from_a_turn_drives_terminal_rather_than_parking_the_reader() {
        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (_, _, _) =>
            Task.FromResult<IAgyTurnProcess>(new ThrowingWaitForExitTurnProcess());

        await using var rt = new AntigravityHostedAgentRuntime(spawnTurn: spawn, logger: NullLogger.Instance);
        var read = Task.Run(async () => { await foreach (var _ in rt.ReadOutputAsync()) { } });

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

        // The whole assertion: this completes only if Terminal was entered. A swallowed cancellation
        // leaves it parked forever.
        await read.WaitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(rt.ExitCode).IsNotEqualTo(0);
    }

    /// <summary>A turn child whose bounded exit-confirmation wait PROPAGATES a cancellation instead of
    /// returning silently — the shape the test above exists for. Everything else about it is an
    /// ordinary clean turn, so the only thing under test is what the worker does with that
    /// exception.</summary>
    sealed class ThrowingWaitForExitTurnProcess : IAgyTurnProcess {
        public int  Pid       => 4246;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

#pragma warning disable CS1998 // no await on the happy path — the lines are already in hand
        public async IAsyncEnumerable<string> ReadLinesAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
            yield return $$$"""{"event":"init","conversation_id":"{{{FixedConversationId}}}","init":{"cwd":"/w"}}""";
            yield return $$$"""{"event":"result","result":{"conversation_id":"{{{FixedConversationId}}}","status":"SUCCESS"}}""";

            HasExited = true;
            ExitCode  = 0;
        }
#pragma warning restore CS1998

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            throw new OperationCanceledException("this implementation propagates its own internal timeout");

        public Task TerminateAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public ValueTask DisposeAsync()                      => ValueTask.CompletedTask;
    }

    [Test]
    public async Task A_turn_that_settles_without_a_conversation_id_faults_the_barrier_instead_of_hanging() {
        // A turn can settle cleanly (→ Idle, healthy — no EnterTerminal call at all) having never seen
        // a non-empty conversation_id on its init line. A first cut of rule (e) only faulted the
        // barrier from EnterTerminal, so a factory correctly awaiting WaitForConversationIdAsync would
        // hang forever here even though the runtime itself is perfectly healthy.
        await using var rt = FakeRuntime(turn: FakeTurn.NormalWithoutConversationId);

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard));

        // Confirms this genuinely is the "healthy but never resolved an id" case, not a disguised
        // Terminal transition — HasExited must still read the between-turns-alive value. The barrier
        // fault (awaited above) runs slightly BEFORE the phase settles to Idle, so give the worker a
        // moment to finish that transition (matches this file's existing "let it settle" convention).
        await Task.Delay(100);
        await Assert.That(rt.HasExited).IsFalse();
    }
}
