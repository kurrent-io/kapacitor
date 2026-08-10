// test/Capacitor.Cli.Tests.Unit/Services/PiRpcHostedAgentRuntimeTests.cs
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using static Capacitor.Cli.Tests.Unit.Services.PiRpcRuntimeFakes;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Exercises <see cref="PiRpcHostedAgentRuntime"/> against <see cref="FakePiRpcProcess"/> — no real
/// <c>pi</c> process is spawned.
///
/// <para>Unlike <c>AntigravityRuntimeLifecycleTests</c>, there is no phase machine to pin here: Pi's
/// child is long-lived, so liveness is the process's own. What replaces it as the highest-risk
/// surface is the <b>ready barrier</b> — <c>StartAsync</c> must not return before the session
/// identity is real, and a barrier that can hang wedges the orchestrator instead of failing a
/// launch. Every path that can end the handshake therefore gets its own test (answered, unanswered
/// + process exit, unanswered + deadline), and two of them are mutation-pinned in the task report.</para>
///
/// <para><b>Ordering discipline in these tests.</b> The fake's stdout is a channel, so a
/// <c>Push</c> is not observed synchronously. Nothing here asserts "not yet" against a bare delay
/// where a FIFO fact is available instead: to prove the pump has consumed line N, the tests push a
/// sentinel line N+1 whose envelope they then await — channel FIFO makes that a proof, a sleep does
/// not.</para>
/// </summary>
public class PiRpcHostedAgentRuntimeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    static async Task<AcpEventEnvelope> NextEnvelopeAsync(PiRpcHostedAgentRuntime runtime) {
        using var cts = new CancellationTokenSource(HangGuard);

        return await runtime.Envelopes.ReadAsync(cts.Token);
    }

    // ---- Ready barrier ----

    [Test]
    public async Task Ready_barrier_resolves_the_session_identity_from_get_state() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.AcpSessionId).IsEqualTo(SessionId);
        await Assert.That(rt.ResolvedModel).IsEqualTo(StateModelId);
        await Assert.That(rt.Cwd).IsEqualTo("/w");

        // The handshake command itself: sent by the constructor, correlated on the pinned id.
        await Assert.That(FirstCommandId(proc, "get_state")).IsEqualTo("init-state");
    }

    /// <summary>Security/correctness regression: <see cref="PiRpcHostedAgentRuntime.ResolvedModel"/>
    /// is read by the orchestrator as a CONFIRMED-applied-model signal, so a <c>get_state</c> that
    /// carries no model must resolve to null — the <see cref="IAcpTranscriptSource"/> contract's
    /// "null ⇒ vendor default applies" — NEVER the merely-requested model. Reporting the requested
    /// model here would misrepresent an unconfirmed value as confirmed.</summary>
    [Test]
    public async Task Ready_barrier_resolves_null_model_when_the_state_carries_none() {
        var (rt, _) = NewRuntime(stateResponse: GetStateResponse(modelId: null));
        await using var __ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.AcpSessionId).IsEqualTo(SessionId);
        await Assert.That(rt.ResolvedModel).IsNull();
    }

    /// <summary>MUTATION-PINNED (task report, guard (a)). A child that dies before answering
    /// <c>get_state</c> must FAULT the barrier — a hang here wedges the factory's
    /// <c>StartAsync</c>, and with it the orchestrator's whole launch path.</summary>
    [Test]
    public async Task Ready_barrier_faults_when_the_process_exits_before_answering() {
        var (rt, proc) = NewRuntime(answerGetState: false);
        await using var _ = rt;

        proc.EndOfStream(exitCode: 3);

        // InvalidOperationException, NOT a bare Exception: a barrier that HUNG would surface the
        // hang guard's own TimeoutException, which a bare `Exception` assertion would happily
        // accept — turning the one test that exists to prove "never hangs" into its opposite.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(rt.AcpSessionId).IsEqualTo("");
    }

    [Test]
    public async Task Ready_barrier_faults_when_the_deadline_elapses() {
        // The child stays alive and simply never answers — only the deadline can end this.
        var (rt, _) = NewRuntime(answerGetState: false, readyDeadline: TimeSpan.FromMilliseconds(200));
        await using var __ = rt;

        // InvalidOperationException, NOT a bare Exception: a barrier that HUNG would surface the
        // hang guard's own TimeoutException, which a bare `Exception` assertion would happily
        // accept — turning the one test that exists to prove "never hangs" into its opposite.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));
    }

    /// <summary>The deadline is not optional — an alive-but-silent child (spawned, hung before its
    /// first read, never exiting) trips NO other resolver, so an omitted deadline would leave that
    /// shape with no way out of the barrier at all. This pins the constant rather than waiting it
    /// out; the behavioural half is
    /// <see cref="Ready_barrier_faults_when_the_deadline_elapses"/>, which injects a short one.</summary>
    [Test]
    public async Task A_ready_deadline_always_applies_even_when_the_caller_omits_one() {
        await Assert.That(PiRpcHostedAgentRuntime.DefaultReadyDeadline).IsEqualTo(TimeSpan.FromSeconds(30));

        // Constructing with no readyDeadline must not throw and must not resolve the barrier early;
        // the fault itself arrives 30s later, which is deliberately not waited on here.
        var (rt, _) = NewRuntime(answerGetState: false, readyDeadline: null);
        await using var __ = rt;

        await Assert.That(rt.WaitForSessionReadyAsync(CancellationToken.None).IsCompleted).IsFalse();
    }

    [Test]
    public async Task Ready_barrier_faults_when_the_state_response_carries_no_session_id() {
        var (rt, _) = NewRuntime(stateResponse: GetStateResponse(sessionId: null));
        await using var __ = rt;

        // Fail closed: binding a transcript to "" is a silent, permanent correlation break.
        // InvalidOperationException, NOT a bare Exception: a barrier that HUNG would surface the
        // hang guard's own TimeoutException, which a bare `Exception` assertion would happily
        // accept — turning the one test that exists to prove "never hangs" into its opposite.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));
    }

    // ---- Read pump ----

    [Test]
    public async Task Event_frames_become_transcript_envelopes() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        proc.Push(AssistantText("hello from pi"));

        var env = await NextEnvelopeAsync(rt);

        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(env.Text).IsEqualTo("hello from pi");
        await Assert.That(env.Model).IsEqualTo(StateModelId);
    }

    [Test]
    public async Task Agent_produced_envelopes_advance_the_activity_clock() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        var clock = new AgentActivityClock(TimeProvider.System);
        rt.ActivityClock = clock;
        var before = clock.ActivitySeq;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        proc.Push(AssistantText("working"));
        await NextEnvelopeAsync(rt);

        await Assert.That(clock.ActivitySeq).IsGreaterThan(before);
    }

    [Test]
    public async Task Response_frames_never_reach_the_transcript() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The handshake response has already been pumped by the time the barrier resolved; the
        // sentinel after it proves the transcript's FIRST item is the assistant text, i.e. nothing
        // from the response frame was ever written.
        proc.Push(AssistantText("after the response"));

        var env = await NextEnvelopeAsync(rt);

        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(env.Text).IsEqualTo("after the response");
    }

    // ---- Sending input ----

    [Test]
    public async Task SendUserInputAsync_emits_a_user_message_then_writes_a_prompt_command() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.SendUserInputAsync("do the thing").WaitAsync(HangGuard);

        var env = await NextEnvelopeAsync(rt);
        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(env.Text).IsEqualTo("do the thing");

        var prompt = proc.Writes.Single(w => w.Contains("\"type\":\"prompt\"", StringComparison.Ordinal));
        await Assert.That(prompt).Contains("do the thing");
        await Assert.That(prompt).Contains("\"streamingBehavior\":\"followUp\"");
    }

    [Test]
    public async Task A_rejected_prompt_emits_a_system_note() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.SendUserInputAsync("do the thing").WaitAsync(HangGuard);

        var promptId = FirstCommandId(proc, "prompt");
        await Assert.That(promptId).IsNotNull();
        proc.Push(PromptResponse(promptId!, success: false, error: "already streaming"));

        var echo = await NextEnvelopeAsync(rt);
        await Assert.That(echo.Kind).IsEqualTo(AcpEventKind.UserMessage);

        var note = await NextEnvelopeAsync(rt);
        await Assert.That(note.Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(note.Text).Contains("already streaming");
    }

    /// <summary>MUTATION-PINNED (task report, guard (b)). Pi echoes our own prompt back as a
    /// user-role <c>message_end</c>; without the dedupe the viewer sees every message they sent
    /// twice.</summary>
    [Test]
    public async Task The_echo_of_a_prompt_we_sent_is_not_duplicated() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.SendUserInputAsync("do the thing").WaitAsync(HangGuard);

        proc.Push(UserMessage("do the thing"));      // Pi's echo — must be dropped
        proc.Push(AssistantText("on it"));           // sentinel: proves the echo was consumed, not merely late

        var first = await NextEnvelopeAsync(rt);
        await Assert.That(first.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(first.Text).IsEqualTo("do the thing");

        var second = await NextEnvelopeAsync(rt);
        await Assert.That(second.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(second.Text).IsEqualTo("on it");
    }

    [Test]
    public async Task An_echo_is_consumed_once_so_a_repeated_prompt_still_renders_twice() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.SendUserInputAsync("again").WaitAsync(HangGuard);

        proc.Push(UserMessage("again"));   // consumes the one remembered prompt
        proc.Push(UserMessage("again"));   // nothing left to match — this is a genuine second message
        proc.Push(AssistantText("done"));

        var first = await NextEnvelopeAsync(rt);
        await Assert.That(first.Kind).IsEqualTo(AcpEventKind.UserMessage);   // our own send-time emit

        var second = await NextEnvelopeAsync(rt);
        await Assert.That(second.Kind).IsEqualTo(AcpEventKind.UserMessage);  // the unmatched echo
        await Assert.That(second.Text).IsEqualTo("again");

        var third = await NextEnvelopeAsync(rt);
        await Assert.That(third.Kind).IsEqualTo(AcpEventKind.AssistantText);
    }

    /// <summary>A write that fails must not leave its text in the echo memory: the echo can never
    /// arrive, and a stale entry would silently swallow the NEXT genuine user message with the same
    /// text — a retry of exactly what just failed being the likeliest next thing to happen.</summary>
    [Test]
    public async Task A_failed_send_does_not_leave_a_stale_echo_entry_behind() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        proc.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(async () => await rt.SendUserInputAsync("retry me"));
        proc.FailWrites = false;

        var firstEcho = await NextEnvelopeAsync(rt);
        await Assert.That(firstEcho.Kind).IsEqualTo(AcpEventKind.UserMessage);

        var note = await NextEnvelopeAsync(rt);
        await Assert.That(note.Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(note.Text).Contains("could not be delivered");

        // The stale entry, if any, would swallow this genuine user message.
        proc.Push(UserMessage("retry me"));
        proc.Push(AssistantText("sentinel"));

        var kept = await NextEnvelopeAsync(rt);
        await Assert.That(kept.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(kept.Text).IsEqualTo("retry me");
    }

    [Test]
    public async Task A_user_message_we_did_not_send_reaches_the_transcript() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        proc.Push(UserMessage("typed straight into pi"));

        var env = await NextEnvelopeAsync(rt);

        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(env.Text).IsEqualTo("typed straight into pi");
    }

    // ---- Keys, raw input, resize ----

    [Test]
    public async Task Escape_sends_an_abort_command_and_other_keys_are_ignored() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.SendSpecialKeyAsync("escape").WaitAsync(HangGuard);
        await rt.SendSpecialKeyAsync("ctrl-r").WaitAsync(HangGuard);

        var aborts = proc.Writes.Count(w => w.Contains("\"type\":\"abort\"", StringComparison.Ordinal));

        await Assert.That(aborts).IsEqualTo(1);
        await Assert.That(proc.Writes.Count).IsEqualTo(2);   // get_state + the one abort
    }

    [Test]
    public async Task Raw_input_is_unsupported_and_resize_is_a_no_op() {
        var (rt, _) = NewRuntime();
        await using var __ = rt;

        await Assert.That(rt.EmitsTerminalOutput).IsFalse();
        await Assert.That(rt.Vendor).IsEqualTo("pi");

        await Assert.ThrowsAsync<NotSupportedException>(async () => await rt.SendRawInputAsync([1, 2, 3]));

        rt.Resize(120, 40);   // no-op: must not throw
    }

    // ---- Turn idleness ----

    [Test]
    public async Task WaitForTurnIdleAsync_completes_immediately_when_the_agent_is_idle() {
        var (rt, _) = NewRuntime();
        await using var __ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);
    }

    [Test]
    public async Task WaitForTurnIdleAsync_waits_for_the_next_agent_settled() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        proc.Push(AgentStart);
        proc.Push(AssistantText("thinking"));         // FIFO sentinel: agent_start has been consumed
        await NextEnvelopeAsync(rt);

        var idle = rt.WaitForTurnIdleAsync(CancellationToken.None);
        await Assert.That(idle.IsCompleted).IsFalse();

        proc.Push(AgentSettled);
        await idle.WaitAsync(HangGuard);
    }

    [Test]
    public async Task An_already_streaming_session_is_busy_from_the_handshake() {
        var (rt, proc) = NewRuntime(stateResponse: GetStateResponse(isStreaming: true));
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        var idle = rt.WaitForTurnIdleAsync(CancellationToken.None);
        await Assert.That(idle.IsCompleted).IsFalse();

        proc.Push(AgentSettled);
        await idle.WaitAsync(HangGuard);
    }

    [Test]
    public async Task Going_terminal_releases_a_turn_idle_waiter() {
        var (rt, proc) = NewRuntime(stateResponse: GetStateResponse(isStreaming: true));
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        var idle = rt.WaitForTurnIdleAsync(CancellationToken.None);
        proc.EndOfStream();

        // A waiter parked on a settle that will never come is a hang, not a stop.
        await idle.WaitAsync(HangGuard);
    }

    /// <summary>A cancelled waiter must not stay in the list — it is the one unbounded collection
    /// here, and the orchestrator's periodic borrowed-snapshot refresh cancels one per cycle.</summary>
    [Test]
    public async Task A_cancelled_turn_idle_waiter_is_removed_from_the_list() {
        var (rt, proc) = NewRuntime(stateResponse: GetStateResponse(isStreaming: true));
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        using var cts = new CancellationTokenSource();
        var abandoned = rt.WaitForTurnIdleAsync(cts.Token);
        await Assert.That(rt.TurnIdleWaiterCount).IsEqualTo(1);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await abandoned.WaitAsync(HangGuard));

        await Assert.That(rt.TurnIdleWaiterCount).IsEqualTo(0);

        // A later settle must still work, and must not trip over the removed waiter.
        var live = rt.WaitForTurnIdleAsync(CancellationToken.None);
        proc.Push(AgentSettled);
        await live.WaitAsync(HangGuard);
    }

    /// <summary>F3's race: a command registered AFTER <c>EnterTerminal</c> took its key snapshot
    /// would park forever — the send's response observer never completes, and the handshake's own
    /// registration racing a synchronously-dead child would burn the whole dispose join budget.</summary>
    [Test]
    public async Task A_command_registered_after_terminal_is_faulted_rather_than_parked() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        var read = Task.Run(async () => { await foreach (var _unused in rt.ReadOutputAsync()) { } });
        proc.EndOfStream();
        await read.WaitAsync(HangGuard);   // terminal has genuinely been entered

        await rt.SendUserInputAsync("lands after the end").WaitAsync(HangGuard);

        await Assert.That(rt.PendingCommandCount).IsEqualTo(0);
    }

    // ---- Stopping ----

    [Test]
    public async Task RequestGracefulStopAsync_aborts_then_terminates_after_the_grace() {
        var (rt, proc) = NewRuntime(stopGrace: TimeSpan.FromMilliseconds(50));
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.RequestGracefulStopAsync().WaitAsync(HangGuard);

        await Assert.That(proc.Writes.Any(w => w.Contains("\"type\":\"abort\"", StringComparison.Ordinal))).IsTrue();
        await Assert.That(proc.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    // ---- Terminal ----

    [Test]
    public async Task ReadOutputAsync_parks_until_terminal_then_ends() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        var read = Task.Run(async () => { await foreach (var _unused in rt.ReadOutputAsync()) { } });

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        proc.Push(AssistantText("still going"));
        await NextEnvelopeAsync(rt);

        // Ending this stream is what drives the orchestrator's finalize — it must not end while live.
        await Assert.That(read.IsCompleted).IsFalse();

        proc.EndOfStream();

        await read.WaitAsync(HangGuard);
        await Assert.That(read.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Terminal_completes_the_transcript_and_never_emits_session_ended() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        proc.Push(AssistantText("last words"));
        proc.EndOfStream();

        var kinds = new List<string>();
        using var cts = new CancellationTokenSource(HangGuard);

        try {
            await foreach (var env in rt.Envelopes.ReadAllAsync(cts.Token))
                kinds.Add(env.Kind);
        } catch (ChannelClosedException) {
            // The reader completing IS the assertion below; ReadAllAsync ends normally on completion.
        }

        await Assert.That(kinds).Contains(AcpEventKind.AssistantText);
        await Assert.That(kinds).DoesNotContain(AcpEventKind.SessionEnded);
    }

    [Test]
    public async Task HasExited_and_ExitCode_delegate_to_the_process() {
        var (rt, proc) = NewRuntime();
        await using var _ = rt;

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsFalse();
        await Assert.That(rt.ExitCode).IsNull();
        await Assert.That(rt.Pid).IsEqualTo(proc.Pid);

        proc.EndOfStream(exitCode: 7);

        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(rt.ExitCode).IsEqualTo(7);
    }

    // ---- Disposal ----

    [Test]
    public async Task DisposeAsync_is_idempotent_and_invokes_the_callback_exactly_once() {
        var disposals = 0;
        var (rt, proc) = NewRuntime(onDisposed: () => Interlocked.Increment(ref disposals));

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rt.DisposeAsync().AsTask().WaitAsync(HangGuard);
        await rt.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(disposals).IsEqualTo(1);
        await Assert.That(proc.DisposeCalls).IsEqualTo(1);
    }
}
