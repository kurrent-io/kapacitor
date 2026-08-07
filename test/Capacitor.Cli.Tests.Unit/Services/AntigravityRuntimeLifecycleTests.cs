// test/Capacitor.Cli.Tests.Unit/Services/AntigravityRuntimeLifecycleTests.cs
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds);
        _ = rt.SendUserInputAsync("hello");

        // If TerminateAsync took the turn gate, this would hang forever: the turn holds the
        // gate, Terminal could never be entered, and the park would never resolve.
        await rt.TerminateAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(rt.HasExited).IsTrue();
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

    [Test]
    public async Task A_full_queue_rejects_further_input_without_spawning() {
        var spawns = 0;
        await using var rt = FakeRuntime(turn: FakeTurn.NeverEnds, onSpawn: _ => spawns++, queueCap: 1);

        // Turn 1 is picked up immediately and never ends, holding the gate — turn 2 fills the
        // 1-deep queue, and a 3rd send has nowhere to go.
        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (spawns < 1 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Assert.That(spawns).IsEqualTo(1);

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);
        await rt.SendUserInputAsync("three").WaitAsync(HangGuard);
        await Task.Delay(100);

        // Only turn 1 ever spawned — turn 2 sits in the (capacity-1) queue and turn 3 was dropped.
        await Assert.That(spawns).IsEqualTo(1);
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
}
