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

    [Test]
    public async Task CancelLease_a_second_time_after_redelivery_does_not_requeue_again() {
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
        enumeratorB.Current.CancelLease(); // second cancel of the SAME envelope: must not requeue again
        channel.TransferConsumer();
        await enumeratorB.DisposeAsync();

        using var ctsC = new CancellationTokenSource();
        var enumeratorC = channel.ConsumeAsync(ctsC.Token).GetAsyncEnumerator();
        var moveNextC = enumeratorC.MoveNextAsync();
        await ctsC.CancelAsync();
        await Assert.That(async () => await moveNextC.AsTask().WaitAsync(Bounded))
            .Throws<OperationCanceledException>(); // lost, not redelivered a second time
        await enumeratorC.DisposeAsync();
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
