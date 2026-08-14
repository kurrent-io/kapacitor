using System.Runtime.CompilerServices;

namespace Capacitor.App.Services.Mutation;

/// One outstanding delivery of an envelope: Ack consumes it permanently, CancelLease requeues it at the front at most once per envelope.
public sealed class OutcomeLease {
    readonly OutcomeChannel _owner;
    readonly OutcomeChannel.Entry _entry;
    readonly long _token;

    internal OutcomeLease(OutcomeChannel owner, OutcomeChannel.Entry entry, long token) {
        _owner = owner;
        _entry = entry;
        _token = token;
    }

    public OutcomeEnvelope Envelope => _entry.Envelope;

    public void Ack() => _owner.Resolve(_entry, _token, requeue: false);
    public void CancelLease() => _owner.Resolve(_entry, _token, requeue: true);
}

/// Leased FIFO with exactly one active consumer; TransferConsumer hands off atomically without disturbing an already-yielded, unresolved lease.
public sealed class OutcomeChannel {
    internal sealed class Entry(OutcomeEnvelope envelope) {
        public readonly OutcomeEnvelope Envelope = envelope;
        public bool Requeued;
        public long ActiveLeaseToken; // 0 = not currently leased (queued or terminally resolved)
    }

    sealed class Session {
        public bool Transferred;
    }

    readonly object _gate = new();
    readonly LinkedList<Entry> _queue = new();
    TaskCompletionSource _wakeup = NewWakeup();
    Session? _current;
    long _leaseTokenCounter;

    static TaskCompletionSource NewWakeup() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Enqueue(OutcomeEnvelope envelope) {
        lock (_gate) {
            _queue.AddLast(new Entry(envelope));
            Wake();
        }
    }

    public IAsyncEnumerable<OutcomeLease> ConsumeAsync(CancellationToken ct) {
        Session session;
        lock (_gate) {
            if (_current is not null) throw new InvalidOperationException("OutcomeChannel already has an active consumer.");
            session = new Session();
            _current = session;
        }
        return ConsumeCoreAsync(session, ct);
    }

    /// Only still-queued envelopes move to the next ConsumeAsync; a lease already yielded to this consumer stays outstanding.
    public void TransferConsumer() {
        lock (_gate) {
            if (_current is null) return;
            _current.Transferred = true;
            _current = null;
            Wake();
        }
    }

    async IAsyncEnumerable<OutcomeLease> ConsumeCoreAsync(Session session, [EnumeratorCancellation] CancellationToken ct) {
        var mine = new List<(Entry Entry, long Token)>();
        try {
            while (true) {
                Entry? entry = null;
                var wait = Task.CompletedTask;
                bool superseded;
                lock (_gate) {
                    superseded = !ReferenceEquals(_current, session);
                    if (!superseded) {
                        if (_queue.Count > 0) {
                            entry = _queue.First!.Value;
                            _queue.RemoveFirst();
                            entry.ActiveLeaseToken = ++_leaseTokenCounter;
                            mine.Add((entry, entry.ActiveLeaseToken));
                        } else {
                            wait = _wakeup.Task;
                        }
                    }
                }
                if (superseded) yield break; // TransferConsumer: graceful end, no exception
                if (entry is not null) {
                    yield return new OutcomeLease(this, entry, entry.ActiveLeaseToken);
                    continue;
                }
                await wait.WaitAsync(ct); // ct firing here propagates as OperationCanceledException
            }
        } finally {
            lock (_gate) {
                if (ReferenceEquals(_current, session)) _current = null;
                if (!session.Transferred) {
                    // ct/teardown: everything this session still holds unresolved gets its one requeue.
                    for (var i = mine.Count - 1; i >= 0; i--) {
                        var (entry, token) = mine[i];
                        if (entry.ActiveLeaseToken != token) continue; // already resolved directly
                        entry.ActiveLeaseToken = 0;
                        if (!entry.Requeued) {
                            entry.Requeued = true;
                            _queue.AddFirst(entry);
                        }
                    }
                }
                Wake();
            }
        }
    }

    internal void Resolve(Entry entry, long token, bool requeue) {
        lock (_gate) {
            if (entry.ActiveLeaseToken != token) return; // stale: this lease was already resolved
            entry.ActiveLeaseToken = 0;
            if (requeue && !entry.Requeued) {
                entry.Requeued = true;
                _queue.AddFirst(entry);
                Wake();
            }
        }
    }

    void Wake() { // caller must hold _gate
        _wakeup.TrySetResult();
        _wakeup = NewWakeup();
    }
}
