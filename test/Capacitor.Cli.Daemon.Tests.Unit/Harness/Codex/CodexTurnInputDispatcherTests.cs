using System.Threading.Channels;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The interactive input serializer (the app-server input design). Drives <see cref="CodexTurnInputDispatcher"/>
/// against a channel-synchronized fake send-sink so each turn/start and turn/steer response — and each
/// turn/completed notification — is released at a controlled moment, exercising the completion-window
/// orderings deterministically (no sleeps).
/// </summary>
public class CodexTurnInputDispatcherTests {
    static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    static CodexTurnInputDispatcher Dispatcher(FakeTurnSink sink) =>
        new(sink.Start, sink.Steer, NullLogger.Instance, CancellationToken.None);

    [Test]
    public async Task Idle_input_dispatches_as_turn_start() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack   = d.EnqueueAsync("hello");
        var start = await sink.NextStart();
        await Assert.That(start.Text).IsEqualTo("hello");
        await Assert.That(ack.IsCompleted).IsFalse();

        start.Started("turn-1");
        await ack.WaitAsync(Guard);
        await Assert.That(d.TurnInFlight).IsTrue();
    }

    [Test]
    public async Task Active_input_dispatches_as_turn_steer() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        (await sink.NextStart()).Started("turn-1");
        await ack1.WaitAsync(Guard);

        var ack2  = d.EnqueueAsync("b");
        var steer = await sink.NextSteer();
        await Assert.That(steer.TurnId).IsEqualTo("turn-1");
        await Assert.That(steer.Text).IsEqualTo("b");

        steer.Accepted();
        await ack2.WaitAsync(Guard);
    }

    [Test]
    public async Task Two_idle_inputs_never_double_start() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        var ack2 = d.EnqueueAsync("b");

        var start = await sink.NextStart();      // only ONE start while the first is pending
        await Assert.That(start.Text).IsEqualTo("a");
        start.Started("turn-1");
        await ack1.WaitAsync(Guard);

        var steer = await sink.NextSteer();      // the second rode a steer, proving no double-start
        await Assert.That(steer.Text).IsEqualTo("b");
        steer.Accepted();
        await ack2.WaitAsync(Guard);

        await Assert.That(sink.StartCount).IsEqualTo(1);
    }

    [Test]
    public async Task Completion_before_start_response_reconciles_to_idle() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack   = d.EnqueueAsync("hello");
        var start = await sink.NextStart();

        d.OnTurnCompleted("turn-1"); // the completion races ahead of the start response
        start.Started("turn-1");      // response arrives after

        await ack.WaitAsync(Guard);
        await Assert.That(d.TurnInFlight).IsFalse();
    }

    [Test]
    public async Task Completion_before_steer_response_success_does_not_retry() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        (await sink.NextStart()).Started("turn-1");
        await ack1.WaitAsync(Guard);

        var ack2  = d.EnqueueAsync("b");
        var steer = await sink.NextSteer();

        d.OnTurnCompleted("turn-1"); // completion arrives before the steer response
        steer.Accepted();             // steer still succeeded → the input rode turn-1

        await ack2.WaitAsync(Guard);
        await Assert.That(sink.StartCount).IsEqualTo(1); // NOT retried as a start
    }

    [Test]
    public async Task Steer_missed_retries_exactly_once_as_start() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        (await sink.NextStart()).Started("turn-1");
        await ack1.WaitAsync(Guard);

        var ack2  = d.EnqueueAsync("b");
        var steer = await sink.NextSteer();
        steer.Missed(); // -32600: the turn ended before the steer landed

        var retry = await sink.NextStart(); // retried as a turn/start for the SAME input
        await Assert.That(retry.Text).IsEqualTo("b");
        retry.Started("turn-2");

        await ack2.WaitAsync(Guard);
        await Assert.That(sink.StartCount).IsEqualTo(2);
        await Assert.That(sink.SteerCount).IsEqualTo(1);
    }

    [Test]
    public async Task Steer_missed_retry_start_error_faults_the_input() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        (await sink.NextStart()).Started("turn-1");
        await ack1.WaitAsync(Guard);

        var ack2  = d.EnqueueAsync("b");
        (await sink.NextSteer()).Missed();

        var retry = await sink.NextStart();
        retry.Error(new InvalidOperationException("boom"));

        await Assert.That(async () => await ack2.WaitAsync(Guard)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Input_text_passes_through_verbatim_no_carriage_return_spray() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack   = d.EnqueueAsync("line1\nline2\nline3");
        var start = await sink.NextStart();

        await Assert.That(start.Text).IsEqualTo("line1\nline2\nline3");
        await Assert.That(start.Text.Contains('\r')).IsFalse();
        start.Started("turn-1");
        await ack.WaitAsync(Guard);
    }

    [Test]
    public async Task FaultAll_faults_queued_and_in_flight_input() {
        var sink = new FakeTurnSink();
        var d    = Dispatcher(sink);

        var ack1 = d.EnqueueAsync("a");
        await sink.NextStart();     // in flight (pending start)
        var ack2 = d.EnqueueAsync("b"); // queued behind it

        d.FaultAll(new ObjectDisposedException("runtime"));

        await Assert.That(async () => await ack2.WaitAsync(Guard)).Throws<ObjectDisposedException>();
    }

    // ── Channel-synchronized fake send-sink ─────────────────────────────────────────────────────

    sealed class FakeTurnSink {
        readonly Channel<StartCall> _starts = Channel.CreateUnbounded<StartCall>();
        readonly Channel<SteerCall> _steers = Channel.CreateUnbounded<SteerCall>();

        int _startCount;
        int _steerCount;
        public int StartCount => Volatile.Read(ref _startCount);
        public int SteerCount => Volatile.Read(ref _steerCount);

        public Task<CodexTurnStarted> Start(string text, CancellationToken ct) {
            Interlocked.Increment(ref _startCount);
            var call = new StartCall(text);
            _starts.Writer.TryWrite(call);
            return call.Response.Task;
        }

        public Task Steer(string turnId, string text, CancellationToken ct) {
            Interlocked.Increment(ref _steerCount);
            var call = new SteerCall(turnId, text);
            _steers.Writer.TryWrite(call);
            return call.Response.Task;
        }

        public async Task<StartCall> NextStart() => await _starts.Reader.ReadAsync().AsTask().WaitAsync(Guard);
        public async Task<SteerCall> NextSteer() => await _steers.Reader.ReadAsync().AsTask().WaitAsync(Guard);
    }

    sealed class StartCall(string text) {
        public string Text { get; } = text;
        public TaskCompletionSource<CodexTurnStarted> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Started(string turnId, string status = "inProgress") => Response.TrySetResult(new CodexTurnStarted(turnId, status));
        public void Error(Exception ex) => Response.TrySetException(ex);
    }

    sealed class SteerCall(string turnId, string text) {
        public string TurnId { get; } = turnId;
        public string Text   { get; } = text;
        public TaskCompletionSource Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Accepted() => Response.TrySetResult();
        public void Missed()   => Response.TrySetException(new CodexAppServerRpcException(-32600, "no active turn to steer"));
    }
}
