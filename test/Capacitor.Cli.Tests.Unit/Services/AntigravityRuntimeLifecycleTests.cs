// test/Capacitor.Cli.Tests.Unit/Services/AntigravityRuntimeLifecycleTests.cs
using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Exercises <see cref="AntigravityHostedAgentRuntime"/>'s phase machine end-to-end against
/// <see cref="FakeAgyTurnProcess"/> — no real <c>agy</c> process is spawned. This is the
/// highest-risk runtime in the daemon: unlike every other <c>IHostedAgentRuntime</c>, it has no
/// long-lived child at all — each turn is its own <c>agy -p …</c> invocation, so "the process
/// exited" and "the runtime is done" are NOT the same event, and getting that distinction wrong
/// produces a silent hang rather than a failing test. See the class doc on
/// <see cref="AntigravityHostedAgentRuntime"/> for the four rules these tests pin.
/// </summary>
public class AntigravityRuntimeLifecycleTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    const string FixedConversationId = "fixed-conversation-id";

    /// <summary>How one fake turn behaves, driving <see cref="FakeAgyTurnProcess.ReadLinesAsync"/>.
    /// Pinned exactly per the design brief — a sibling plan reuses this shape.</summary>
    public enum FakeTurn {
        /// <summary>Emits <c>init</c> then a <c>result</c> with <c>status: SUCCESS</c>, then EOFs —
        /// the ordinary clean-turn shape.</summary>
        Normal,

        /// <summary>Emits <c>init</c> only, then EOFs with NO <c>result</c> line at all — the "the
        /// reviewer died mid-turn" shape rule (a) exists for.</summary>
        EofWithoutResult,

        /// <summary>Emits <c>init</c>, then blocks forever until its cancellation token fires — the
        /// "a turn is genuinely still running" shape the deadlock mutation check exercises.</summary>
        NeverEnds,
    }

    /// <summary><see cref="IAgyTurnProcess"/> fake for ONE turn. A fresh instance is handed out by
    /// the injected spawner for every turn, mirroring agy's real exec-per-turn shape — this is never
    /// reused across turns.</summary>
    sealed class FakeAgyTurnProcess(FakeTurn turn, string conversationId) : IAgyTurnProcess {
        public int  Pid            { get; } = 4242;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
            yield return $$$"""{"event":"init","conversation_id":"{{{conversationId}}}","init":{"cwd":"/w"}}""";

            switch (turn) {
                case FakeTurn.Normal:
                    yield return $$$"""{"event":"result","result":{"conversation_id":"{{{conversationId}}}","status":"SUCCESS"}}""";
                    HasExited = true;
                    ExitCode  = 0;
                    break;

                case FakeTurn.EofWithoutResult:
                    // No result line — the child exits having said nothing more. The runtime must
                    // not read this EOF as "turn complete".
                    HasExited = true;
                    ExitCode  = 1;
                    break;

                case FakeTurn.NeverEnds:
                    // Blocks until the runtime's own cancellation (owner-cancel or a deadline) fires —
                    // simulates a turn that is genuinely still running.
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    break;
            }
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            HasExited = true;
            ExitCode ??= -1;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds a runtime whose turn spawner never touches a real process. Signature pinned exactly
    /// per the design brief — a sibling plan reuses this helper directly, so <paramref name="onSpawn"/>
    /// receiving the PROMPT (not a bare notification) and <paramref name="queueCap"/> both matter to
    /// get right here.
    /// </summary>
    static AntigravityHostedAgentRuntime FakeRuntime(
            FakeTurn        turn     = FakeTurn.Normal,
            Action<string>? onSpawn  = null,
            int             queueCap = 64) {
        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (prompt, _, _) => {
            onSpawn?.Invoke(prompt);
            return Task.FromResult<IAgyTurnProcess>(new FakeAgyTurnProcess(turn, FixedConversationId));
        };

        return new AntigravityHostedAgentRuntime(
            spawnTurn: spawn,
            logger: NullLogger.Instance,
            pendingTurnsCapacity: queueCap);
    }

    [Test]
    public async Task Between_turns_the_runtime_is_logically_alive() {
        await using var rt = FakeRuntime();
        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

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
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

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
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

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
        await using var rt = FakeRuntime();
        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);
        var first = rt.AcpSessionId;

        await rt.SendUserInputAsync("two").WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // A changed conversation id means we silently forked the reviewer's history.
        await Assert.That(rt.AcpSessionId).IsEqualTo(first);
    }

    [Test]
    public async Task Spawner_receives_the_prompt_text() {
        var prompts = new List<string>();
        await using var rt = FakeRuntime(onSpawn: p => prompts.Add(p));

        await rt.SendUserInputAsync("do the review").WaitAsync(HangGuard);
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
}
