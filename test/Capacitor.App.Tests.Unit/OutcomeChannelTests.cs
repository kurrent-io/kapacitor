using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Tests.Unit;

public class OutcomeChannelTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    static OutcomeEnvelope Env(string tag) =>
        new(new MutationRequest(MutationVerb.Install, "default", "https://example.test", tag),
            new MutationOutcome.Succeeded());

    static Task<bool> BoundedMoveNext(IAsyncEnumerator<OutcomeLease> e) =>
        e.MoveNextAsync().AsTask().WaitAsync(Bounded);

    [Test]
    public async Task Two_enqueues_without_a_consumer_surface_in_FIFO_order_exactly_once() {
        var channel = new OutcomeChannel();
        var e1 = Env("e1");
        var e2 = Env("e2");
        channel.Enqueue(e1);
        channel.Enqueue(e2);

        using var cts = new CancellationTokenSource();
        var enumerator = channel.ConsumeAsync(cts.Token).GetAsyncEnumerator();
        try {
            await Assert.That(await BoundedMoveNext(enumerator)).IsTrue();
            await Assert.That(enumerator.Current.Envelope).IsEqualTo(e1);
            enumerator.Current.Ack();

            await Assert.That(await BoundedMoveNext(enumerator)).IsTrue();
            await Assert.That(enumerator.Current.Envelope).IsEqualTo(e2);
            enumerator.Current.Ack();
        } finally {
            await enumerator.DisposeAsync();
        }
    }

    [Test]
    public async Task Late_enqueue_after_the_consumer_drained_empty_wakes_it() {
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();
        var enumerator = channel.ConsumeAsync(cts.Token).GetAsyncEnumerator();
        try {
            var moveNext = enumerator.MoveNextAsync(); // queue empty: the synchronous prefix guarantees this suspends on the wakeup before Enqueue runs below
            var env = Env("late");
            channel.Enqueue(env);

            await Assert.That(await moveNext.AsTask().WaitAsync(Bounded)).IsTrue();
            await Assert.That(enumerator.Current.Envelope).IsEqualTo(env);
            enumerator.Current.Ack();
        } finally {
            await enumerator.DisposeAsync();
        }
    }

    [Test]
    public async Task Enqueue_racing_TransferConsumer_delivers_to_exactly_one_consumer() {
        var channel = new OutcomeChannel();
        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        var moveNextA = enumeratorA.MoveNextAsync(); // synchronous prefix: A is a registered waiter before the race below starts

        var env = Env("race");
        await Task.WhenAll(
            Task.Run(() => channel.Enqueue(env)),
            Task.Run(channel.TransferConsumer));

        var gotA = await moveNextA.AsTask().WaitAsync(Bounded); // either A or a fresh B wins the item — "exactly one" is under test, not which one
        if (gotA) {
            await Assert.That(enumeratorA.Current.Envelope).IsEqualTo(env);
            enumeratorA.Current.Ack();
            await enumeratorA.DisposeAsync();

            using var ctsB = new CancellationTokenSource();
            var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
            var moveNextB = enumeratorB.MoveNextAsync();
            await ctsB.CancelAsync();
            await Assert.That(async () => await moveNextB.AsTask().WaitAsync(Bounded))
                .Throws<OperationCanceledException>();
            await enumeratorB.DisposeAsync();
        } else {
            await enumeratorA.DisposeAsync();

            using var ctsB = new CancellationTokenSource();
            var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
            try {
                await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
                await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env);
                enumeratorB.Current.Ack();
            } finally {
                await enumeratorB.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task CancelLease_requeues_once_and_ack_after_redelivery_consumes_without_further_redelivery() {
        var channel = new OutcomeChannel();
        var env = Env("cancel-then-ack");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        await Assert.That(enumeratorA.Current.Envelope).IsEqualTo(env);
        enumeratorA.Current.CancelLease(); // requeue #1 (the only one this envelope ever gets)

        channel.TransferConsumer();
        await enumeratorA.DisposeAsync();

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
        await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env); // redelivered to the next consumer exactly once
        enumeratorB.Current.Ack();

        var moveNextB2 = enumeratorB.MoveNextAsync();
        await ctsB.CancelAsync();
        await Assert.That(async () => await moveNextB2.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // nothing left: not duplicated
        await enumeratorB.DisposeAsync();
    }

    [Test, NotInParallel]
    public async Task CancelLease_a_second_time_after_redelivery_is_consumed_with_a_logged_warning_not_silently_dropped() {
        var channel = new OutcomeChannel();
        var env = Env("double-cancel");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        enumeratorA.Current.CancelLease(); // first cancel: uses the one guaranteed requeue
        channel.TransferConsumer();
        await enumeratorA.DisposeAsync();

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
        await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env);

        using var capture = ConsoleOutput.StartErrorCapture();
        enumeratorB.Current.CancelLease(); // second cancel of the SAME envelope: consumed-with-log, must not requeue again
        channel.TransferConsumer();
        await enumeratorB.DisposeAsync();

        await Assert.That(capture.GetCapturedError()).Contains("double-cancel"); // never a silent drop

        using var ctsC = new CancellationTokenSource();
        var enumeratorC = channel.ConsumeAsync(ctsC.Token).GetAsyncEnumerator();
        var moveNextC = enumeratorC.MoveNextAsync();
        await ctsC.CancelAsync();
        await Assert.That(async () => await moveNextC.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // lost, not redelivered a second time
        await enumeratorC.DisposeAsync();

        // channel remains functional afterward: a fresh envelope still enqueues, delivers, and acks normally.
        var env2 = Env("after-double-cancel");
        channel.Enqueue(env2);
        using var ctsD = new CancellationTokenSource();
        var enumeratorD = channel.ConsumeAsync(ctsD.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorD)).IsTrue();
        await Assert.That(enumeratorD.Current.Envelope).IsEqualTo(env2);
        enumeratorD.Current.Ack();
        await enumeratorD.DisposeAsync();
    }

    [Test, NotInParallel]
    public async Task Teardown_after_redelivery_with_the_lease_still_outstanding_is_consumed_with_a_logged_warning() {
        var channel = new OutcomeChannel();
        var env = Env("implicit-double-abandon");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        enumeratorA.Current.CancelLease(); // first abandonment (explicit): uses the one guaranteed requeue
        channel.TransferConsumer();
        await enumeratorA.DisposeAsync();

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
        await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env);
        // deliberately neither Ack nor CancelLease — tear B down via ct with the redelivered lease still outstanding

        var moveNextB2 = enumeratorB.MoveNextAsync();
        using var capture = ConsoleOutput.StartErrorCapture();
        await ctsB.CancelAsync();
        await Assert.That(async () => await moveNextB2.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>();
        await enumeratorB.DisposeAsync();

        await Assert.That(capture.GetCapturedError()).Contains("implicit-double-abandon"); // second abandonment logged, not silent

        using var ctsC = new CancellationTokenSource();
        var enumeratorC = channel.ConsumeAsync(ctsC.Token).GetAsyncEnumerator();
        var moveNextC = enumeratorC.MoveNextAsync();
        await ctsC.CancelAsync();
        await Assert.That(async () => await moveNextC.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // next consumer sees an empty queue
        await enumeratorC.DisposeAsync();

        // channel remains functional afterward.
        var env2 = Env("after-implicit-double-abandon");
        channel.Enqueue(env2);
        using var ctsD = new CancellationTokenSource();
        var enumeratorD = channel.ConsumeAsync(ctsD.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorD)).IsTrue();
        await Assert.That(enumeratorD.Current.Envelope).IsEqualTo(env2);
        enumeratorD.Current.Ack();
        await enumeratorD.DisposeAsync();
    }

    [Test]
    public async Task Teardown_with_two_outstanding_leases_requeues_both_in_original_FIFO_order() {
        var channel = new OutcomeChannel();
        var envA = Env("multi-a");
        var envB = Env("multi-b");
        channel.Enqueue(envA);
        channel.Enqueue(envB);

        using var cts1 = new CancellationTokenSource();
        var enumerator1 = channel.ConsumeAsync(cts1.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumerator1)).IsTrue();
        await Assert.That(enumerator1.Current.Envelope).IsEqualTo(envA); // dequeued first, left outstanding
        await Assert.That(await BoundedMoveNext(enumerator1)).IsTrue();
        await Assert.That(enumerator1.Current.Envelope).IsEqualTo(envB); // dequeued second, left outstanding too

        var moveNextAgain = enumerator1.MoveNextAsync(); // queue now empty: suspends
        await cts1.CancelAsync();
        await Assert.That(async () => await moveNextAgain.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>();
        await enumerator1.DisposeAsync();

        using var cts2 = new CancellationTokenSource();
        var enumerator2 = channel.ConsumeAsync(cts2.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumerator2)).IsTrue();
        await Assert.That(enumerator2.Current.Envelope).IsEqualTo(envA); // FIFO preserved: A before B
        enumerator2.Current.Ack();
        await Assert.That(await BoundedMoveNext(enumerator2)).IsTrue();
        await Assert.That(enumerator2.Current.Envelope).IsEqualTo(envB);
        enumerator2.Current.Ack();
        await enumerator2.DisposeAsync();
    }

    [Test]
    public async Task Teardown_with_one_acked_and_one_outstanding_lease_redelivers_only_the_outstanding_one() {
        var channel = new OutcomeChannel();
        var envA = Env("ack-a");
        var envB = Env("leave-b");
        channel.Enqueue(envA);
        channel.Enqueue(envB);

        using var cts1 = new CancellationTokenSource();
        var enumerator1 = channel.ConsumeAsync(cts1.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumerator1)).IsTrue();
        await Assert.That(enumerator1.Current.Envelope).IsEqualTo(envA);
        enumerator1.Current.Ack(); // A resolved before teardown

        await Assert.That(await BoundedMoveNext(enumerator1)).IsTrue();
        await Assert.That(enumerator1.Current.Envelope).IsEqualTo(envB); // B left outstanding

        var moveNextAgain = enumerator1.MoveNextAsync();
        await cts1.CancelAsync();
        await Assert.That(async () => await moveNextAgain.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>();
        await enumerator1.DisposeAsync();

        using var cts2 = new CancellationTokenSource();
        var enumerator2 = channel.ConsumeAsync(cts2.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumerator2)).IsTrue();
        await Assert.That(enumerator2.Current.Envelope).IsEqualTo(envB); // only B redelivered
        enumerator2.Current.Ack();

        var moveNextC = enumerator2.MoveNextAsync();
        await cts2.CancelAsync();
        await Assert.That(async () => await moveNextC.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // A never resurfaces — it was already acked, not outstanding
        await enumerator2.DisposeAsync();
    }

    [Test]
    public async Task Consumer_ct_cancellation_with_an_unacked_lease_requeues_it() {
        var channel = new OutcomeChannel();
        var env = Env("ct-cancel");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        // deliberately neither Ack nor CancelLease

        var moveNextAAgain = enumeratorA.MoveNextAsync();
        await ctsA.CancelAsync();
        await Assert.That(async () => await moveNextAAgain.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>();
        await enumeratorA.DisposeAsync();

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
        await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env);
        enumeratorB.Current.Ack();
        await enumeratorB.DisposeAsync();
    }

    [Test]
    public async Task Second_concurrent_ConsumeAsync_throws() {
        var channel = new OutcomeChannel();
        using var ctsA = new CancellationTokenSource();
        _ = channel.ConsumeAsync(ctsA.Token); // claims the slot synchronously — no enumeration needed

        using var ctsB = new CancellationTokenSource();
        await Assert.That(() => channel.ConsumeAsync(ctsB.Token)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Ack_after_transfer_of_an_already_presented_envelope_is_consumed_never_requeued() {
        var channel = new OutcomeChannel();
        var env = Env("presented");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        var lease = enumeratorA.Current; // "presented to the user" — held directly, not yet resolved

        channel.TransferConsumer();
        lease.Ack(); // the original, still-valid lease resolves after the handoff

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        var moveNextB = enumeratorB.MoveNextAsync();
        await ctsB.CancelAsync();
        await Assert.That(async () => await moveNextB.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // never redelivered

        await enumeratorA.DisposeAsync();
        await enumeratorB.DisposeAsync();
    }

    [Test]
    public async Task CancelLease_after_transfer_of_a_presented_envelope_still_uses_its_one_requeue() {
        // Ambiguous edge, resolved loss-averse: transfer exempts a lease from forced auto-cancel, not from an explicit CancelLease.
        var channel = new OutcomeChannel();
        var env = Env("cancel-after-transfer");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        var lease = enumeratorA.Current;

        channel.TransferConsumer();
        lease.CancelLease();

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
        await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env);
        enumeratorB.Current.Ack();

        await enumeratorA.DisposeAsync();
        await enumeratorB.DisposeAsync();
    }

    // P1-1: an envelope dequeued-but-never-presented before the transfer must not be permanently
    // leased and lost — when the OLD (transferred-away) enumeration itself terminates without ever
    // acking, the still-unresolved lease gets its one requeue, same as any other implicit-cancel
    // teardown.
    [Test]
    public async Task Old_enumeration_ending_unacked_after_transfer_requeues_the_envelope_exactly_once() {
        var channel = new OutcomeChannel();
        var env = Env("transferred-unacked");
        channel.Enqueue(env);

        using var ctsA = new CancellationTokenSource();
        var enumeratorA = channel.ConsumeAsync(ctsA.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumeratorA)).IsTrue();
        var lease = enumeratorA.Current; // dequeued but never presented — no Ack, no CancelLease

        channel.TransferConsumer();
        await enumeratorA.DisposeAsync(); // the old enumeration ends here, still holding the unresolved lease

        using var ctsB = new CancellationTokenSource();
        var enumeratorB = channel.ConsumeAsync(ctsB.Token).GetAsyncEnumerator();
        try {
            await Assert.That(await BoundedMoveNext(enumeratorB)).IsTrue();
            await Assert.That(enumeratorB.Current.Envelope).IsEqualTo(env); // redelivered to the NEXT consumer
            enumeratorB.Current.Ack();

            // N-resolutions invariant: the envelope resolves EXACTLY once — a late call on the
            // stale original lease (already superseded by the redelivered one) is a silent no-op,
            // never a double free/second requeue.
            lease.Ack();
            lease.CancelLease();

            var moveNextB2 = enumeratorB.MoveNextAsync();
            await ctsB.CancelAsync();
            await Assert.That(async () => await moveNextB2.AsTask().WaitAsync(Bounded))
                .Throws<OperationCanceledException>(); // nothing left: not duplicated
        } finally {
            await enumeratorB.DisposeAsync();
        }
    }

    [Test]
    public async Task TransferConsumer_with_no_active_consumer_is_a_noop() {
        var channel = new OutcomeChannel();
        channel.TransferConsumer(); // must not throw
        channel.Enqueue(Env("after-noop-transfer"));

        using var cts = new CancellationTokenSource();
        var enumerator = channel.ConsumeAsync(cts.Token).GetAsyncEnumerator();
        await Assert.That(await BoundedMoveNext(enumerator)).IsTrue();
        await enumerator.DisposeAsync();
    }
}
