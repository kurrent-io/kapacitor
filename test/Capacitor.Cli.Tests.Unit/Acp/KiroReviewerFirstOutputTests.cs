using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The second half of the launch bound. The factory's deadline covers spawn through the handshake;
/// StartAsync deliberately does NOT await the first turn, so a peer that completes initialize and
/// then wedges — a kiro-cli sitting on an expired credential's browser prompt — would otherwise run
/// forever. Bounded on time-to-first-OUTPUT rather than turn completion, because a real review turn
/// legitimately runs for minutes.
/// </summary>
public class KiroReviewerFirstOutputTests {
    static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    sealed class TrackingProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid        => 4711;
        public bool HasExited  { get; private set; }
        public int? ExitCode   { get; private set; }
        public bool Terminated { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            Terminated = true; HasExited = true; ExitCode = 137;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The fake's OWN envelope builder, not a hand-rolled one. WriteRawFrameAsync writes the value as
    // a whole JSON-RPC frame, so a hand-built params object produces a frame with no "method" — it is
    // never dispatched as session/update, and the "peer emitted output" premise silently fails. That
    // is exactly how a fixture ends up testing the consumer against itself.
    static JsonElement Chunk(string text) =>
        FakeAcpAgent.DefaultAgentMessageChunkUpdate(FakeAcpAgent.FixedSessionId, text);

    static async Task<(TrackingProcess Process, FakeAcpAgent Agent, AcpHostedAgentRuntime Runtime)>
            StartAsync(TimeSpan? firstOutputDeadline, CancellationToken ct,
                       IReadOnlyList<JsonElement>? updates = null) {
        var agent   = new FakeAcpAgent();
        var conn    = new AcpConnection(agent.ClientWriteStream, agent.ClientReadStream, NullLogger.Instance);
        var process = new TrackingProcess();

        // GENUINELY silent. The fake's default prompt script emits an agent_message_chunk, so a
        // plain fake is not the peer this bound is about — an earlier version of this test failed for
        // exactly that reason, and it was the test that was wrong. An empty update list plus a held
        // response models a child that accepted the turn and then produced nothing at all.
        agent.EnqueuePromptScript(
            updates ?? [], JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone());
        agent.HoldPromptResponses = new TaskCompletionSource();

        _ = agent.RunAsync(ct);

        var runtime = new AcpHostedAgentRuntime(
            conn, process, NullLogger.Instance, agentId: "agent-1", vendor: "kiro",
            firstOutputDeadline: firstOutputDeadline);

        // A non-empty prompt is what arms the watchdog — an empty one enqueues no turn at all, which
        // is why a test with an empty prompt could never exercise this.
        await runtime.StartAsync("/abs/wt", "review this", ct).WaitAsync(Guard);

        return (process, agent, runtime);
    }

    /// <summary>
    /// The production shape: handshake succeeds, then nothing. The peer stays alive and silent.
    /// </summary>
    [Test]
    public async Task APeerThatCompletesTheHandshakeThenGoesSilent_IsReaped() {
        using var cts = new CancellationTokenSource(Guard);
        var (process, _, runtime) = await StartAsync(TimeSpan.FromMilliseconds(300), cts.Token);

        var deadline = DateTime.UtcNow + Guard;
        while (!process.Terminated && DateTime.UtcNow < deadline)
            await Task.Delay(25, cts.Token);

        await Assert.That(process.Terminated).IsTrue();
        await runtime.DisposeAsync();
    }

    /// <summary>
    /// The control, and the one that stops the watchdog from being a plain kill-timer: the SAME
    /// handshake with the watchdog disabled is left alone. Without this, an implementation that
    /// reaped every reviewer unconditionally would pass the test above.
    /// </summary>
    [Test]
    public async Task TheSameSilentPeer_IsLeftAloneWhenNoDeadlineIsSet() {
        using var cts = new CancellationTokenSource(Guard);
        var (process, _, runtime) = await StartAsync(firstOutputDeadline: null, cts.Token);

        await Task.Delay(600, cts.Token);

        await Assert.That(process.Terminated).IsFalse();
        await runtime.DisposeAsync();
    }

    /// <summary>
    /// The control that makes the reap test mean something. An implementation that killed
    /// unconditionally whenever a deadline is configured passes both tests above — this is the one it
    /// fails. Same deadline, same held (never-completing) turn; the only difference is that this peer
    /// emits one update, so it must be left alone well past the deadline.
    /// </summary>
    [Test]
    public async Task APeerThatEmitsOutput_SurvivesPastTheDeadline() {
        using var cts = new CancellationTokenSource(Guard);

        var (process, _, runtime) = await StartAsync(
            TimeSpan.FromMilliseconds(300), cts.Token, updates: [Chunk("reviewing")]);

        await Task.Delay(1200, cts.Token);

        await Assert.That(process.Terminated).IsFalse();
        await runtime.DisposeAsync();
    }
}
