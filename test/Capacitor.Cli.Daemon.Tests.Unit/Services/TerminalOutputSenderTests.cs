using System.Collections.Concurrent;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The hosted-agent terminal mirror must reach the server in PTY
/// order and survive a flapping SignalR connection <em>without dropping bytes</em>.
/// An earlier version routed the stream through a single ordered drain loop but capped it with a
/// <c>DropOldest</c> channel — under back-pressure that silently discarded the
/// oldest chunks, severing a redraw-TUI stream mid-escape-sequence (the garbled
/// "Terminal" tab). A later fix makes the queue loss-free: a full channel back-pressures
/// the producer instead of dropping, and the only remaining drop — a send that keeps
/// throwing while the hub reports Connected — is bounded-retried, then counted and
/// logged rather than silently lost. These tests pin those guarantees without
/// standing up a real hub.
/// </summary>
public class TerminalOutputSenderTests {
    static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(5);

    [Test]
    public async Task Chunks_are_delivered_in_enqueue_order() {
        var delivered = new ConcurrentQueue<string>();

        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => true,
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++) {
            await sender.EnqueueAsync("agent-1", $"chunk-{i}", CancellationToken.None);
        }

        sender.Complete();
        await run;

        // Order-sensitive, by index: IsEquivalentTo is permutation-tolerant and
        // would pass for any order, defeating the point of an ordering test.
        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(50);

        for (var i = 0; i < 50; i++) {
            await Assert.That(arr[i]).IsEqualTo($"chunk-{i}");
        }

        await Assert.That(sender.DroppedChunks).IsEqualTo(0L);
    }

    [Test]
    public async Task TryEnqueue_is_nonblocking_and_drops_when_full() {
        // Local-first path: a registered local agent's PTY read loop must never block on a stalled
        // server transport. With no pump draining, the bounded(2) channel fills after two writes;
        // TryEnqueue must return immediately (never block) and drop+count the overflow.
        var sender = new TerminalOutputSender(
            (_, _, _) => Task.CompletedTask,
            isConnected: () => false,
            NullLogger.Instance,
            capacity: 2
        );

        await Assert.That(sender.TryEnqueue("agent-1", "x")).IsTrue();
        await Assert.That(sender.TryEnqueue("agent-1", "y")).IsTrue();
        await Assert.That(sender.TryEnqueue("agent-1", "z")).IsFalse(); // full → dropped, not blocked
        await Assert.That(sender.DroppedChunks).IsEqualTo(1L);
    }

    [Test]
    public async Task Failed_send_while_transport_down_is_held_and_retried_so_order_is_preserved_and_nothing_is_lost() {
        var delivered = new ConcurrentQueue<string>();
        var failsLeft = 5; // first chunk fails 5x (transport "down") before it lands

        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                if (base64 == "first" && Interlocked.Decrement(ref failsLeft) >= 0) {
                    throw new InvalidOperationException("connection is not active");
                }

                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => false, // transport down — failures must be held and retried, not dropped
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        await sender.EnqueueAsync("agent-1", "first", CancellationToken.None);
        await sender.EnqueueAsync("agent-1", "second", CancellationToken.None);
        await sender.EnqueueAsync("agent-1", "third", CancellationToken.None);

        sender.Complete();
        await run;

        // "first" must still arrive before "second"/"third" even though it failed
        // repeatedly: the drain loop holds the head chunk and retries it in place.
        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(3);
        await Assert.That(arr[0]).IsEqualTo("first");
        await Assert.That(arr[1]).IsEqualTo("second");
        await Assert.That(arr[2]).IsEqualTo("third");
        await Assert.That(sender.DroppedChunks).IsEqualTo(0L);
    }

    [Test]
    public async Task A_full_queue_back_pressures_the_producer_instead_of_dropping() {
        var delivered = new ConcurrentQueue<string>();
        var gate            = new TaskCompletionSource();
        var firstSendBegan  = new TaskCompletionSource();
        var firstSend       = true;

        // Capacity 1 + a consumer that blocks on the first send: the channel fills,
        // so the third EnqueueAsync must wait (back-pressure) rather than evicting
        // the oldest queued chunk the way DropOldest did.
        var sender = new TerminalOutputSender(
            async (_, base64, _) => {
                if (firstSend) {
                    firstSend = false;
                    firstSendBegan.TrySetResult();
                    await gate.Task;
                }

                delivered.Enqueue(base64);
            },
            isConnected: () => true,
            NullLogger.Instance,
            capacity: 1,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(CancellationToken.None);

        await sender.EnqueueAsync("agent-1", "c0", CancellationToken.None); // consumer takes it, blocks
        await firstSendBegan.Task;
        await sender.EnqueueAsync("agent-1", "c1", CancellationToken.None); // fills the capacity-1 channel

        // c2 cannot be queued until the consumer drains — EnqueueAsync stays pending.
        var enq2 = sender.EnqueueAsync("agent-1", "c2", CancellationToken.None).AsTask();
        var winner = await Task.WhenAny(enq2, Task.Delay(250));
        await Assert.That(winner).IsNotEqualTo(enq2); // still back-pressured, nothing dropped

        gate.TrySetResult(); // release the consumer; everything drains in order
        await enq2;
        sender.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(3);
        await Assert.That(arr[0]).IsEqualTo("c0");
        await Assert.That(arr[1]).IsEqualTo("c1");
        await Assert.That(arr[2]).IsEqualTo("c2");
        await Assert.That(sender.DroppedChunks).IsEqualTo(0L);
    }

    [Test]
    public async Task Transient_send_failure_while_connected_is_retried_and_eventually_delivered() {
        var delivered = new ConcurrentQueue<string>();
        var failsLeft = 2; // fails twice (< maxConnectedAttempts), then lands

        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                if (base64 == "flaky" && Interlocked.Decrement(ref failsLeft) >= 0) {
                    throw new InvalidOperationException("transient");
                }

                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => true,
            NullLogger.Instance,
            retryDelay: FastRetry,
            maxConnectedAttempts: 5
        );

        var run = sender.RunAsync(CancellationToken.None);

        await sender.EnqueueAsync("agent-1", "flaky", CancellationToken.None);
        await sender.EnqueueAsync("agent-1", "next", CancellationToken.None);

        sender.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        // A transient failure must NOT be dropped — it recovers within the budget.
        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(2);
        await Assert.That(arr[0]).IsEqualTo("flaky");
        await Assert.That(arr[1]).IsEqualTo("next");
        await Assert.That(sender.DroppedChunks).IsEqualTo(0L);
    }

    [Test]
    public async Task Send_that_keeps_failing_while_connected_is_dropped_after_max_attempts_and_counted() {
        var delivered = new ConcurrentQueue<string>();

        // The hub reports Connected but "poison" always throws. Retrying forever
        // would wedge the single shared loop, so after a bounded number of attempts
        // the sender drops that one chunk, counts it, and keeps delivering the rest.
        var sender = new TerminalOutputSender(
            (_, base64, _) => {
                if (base64 == "poison") throw new InvalidOperationException("boom");

                delivered.Enqueue(base64);

                return Task.CompletedTask;
            },
            isConnected: () => true,
            NullLogger.Instance,
            retryDelay: FastRetry,
            maxConnectedAttempts: 3
        );

        var run = sender.RunAsync(CancellationToken.None);

        await sender.EnqueueAsync("agent-1", "poison", CancellationToken.None);
        await sender.EnqueueAsync("agent-1", "after-1", CancellationToken.None);
        await sender.EnqueueAsync("agent-1", "after-2", CancellationToken.None);

        sender.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        var arr = delivered.ToArray();
        await Assert.That(arr.Length).IsEqualTo(2);
        await Assert.That(arr[0]).IsEqualTo("after-1");
        await Assert.That(arr[1]).IsEqualTo("after-2");
        await Assert.That(sender.DroppedChunks).IsEqualTo(1L);
    }

    [Test]
    public async Task Cancellation_stops_the_loop_even_while_holding_a_failing_chunk() {
        using var cts = new CancellationTokenSource();

        var sender = new TerminalOutputSender(
            (_, _, _) => throw new InvalidOperationException("connection is not active"),
            isConnected: () => false, // held (not dropped), so the loop is genuinely stuck until cancelled
            NullLogger.Instance,
            retryDelay: FastRetry
        );

        var run = sender.RunAsync(cts.Token);

        await sender.EnqueueAsync("agent-1", "stuck", CancellationToken.None);
        await Task.Delay(50);
        await cts.CancelAsync();

        // RunAsync must return promptly rather than retrying the failing chunk forever.
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
