using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Outcome of a <c>turn/start</c> dispatch — the server-assigned turn id and its status
/// (<c>inProgress</c> unless the turn came back already terminal).</summary>
internal readonly record struct CodexTurnStarted(string TurnId, string? Status);

/// <summary>
/// The single serializer for interactive hosted-Codex input. The app server accepts a concurrent
/// <c>turn/start</c> without error — the protocol spike proved it does NOT serialize turns for you — so
/// serialization has to live on our side: every input surface enqueues here and ONE dispatcher drains,
/// choosing <c>turn/start</c> when idle and <c>turn/steer</c> when a turn is active.
///
/// <para>Two orthogonal fields, deliberately not one flat state, because a notification can arrive
/// while a request is in flight: <see cref="_pending"/> (the request whose JSON-RPC response is still
/// outstanding, driven only by that response) and <see cref="_lifecycle"/> (Idle/Active, driven only
/// by <c>turn/completed</c>). The dispatcher invariant is on <see cref="_pending"/>: it dispatches the
/// head input only when nothing is pending, so two inputs can never both observe idle and double-start
/// (the exact hazard the server would accept). A steer the server rejects with <c>-32600</c> ("no
/// active turn") is retried EXACTLY ONCE as a <c>turn/start</c> — in the dispatcher, never the
/// enqueuing surface — and never dropped. An input is acknowledged only after the dispatch that
/// actually carried it succeeds.</para>
/// </summary>
internal sealed class CodexTurnInputDispatcher {
    // Sends turn/start with the input as the initial prompt; returns the started turn once the RESPONSE
    // lands. Throws CodexAppServerRpcException on an error response.
    readonly Func<string, CancellationToken, Task<CodexTurnStarted>> _startTurn;
    // Sends turn/steer(expectedTurnId, input); returns when the RESPONSE lands. Throws
    // CodexAppServerRpcException(-32600) when the turn is no longer active (the retryable miss).
    readonly Func<string, string, CancellationToken, Task> _steerTurn;
    // Notified (true/false) as a turn becomes active / goes idle — drives the runtime's turn-in-flight clock.
    readonly Action<bool>?     _onTurnInFlight;
    readonly ILogger           _logger;
    readonly CancellationToken _ct;

    readonly object            _gate = new();
    readonly Queue<InputItem>  _queue = new();
    readonly List<TaskCompletionSource> _settledWaiters = [];

    Pending   _pending   = Pending.None;
    Lifecycle _lifecycle = Lifecycle.Idle;
    string?   _activeTurnId;              // set in Active
    string?   _completedBeforeStartResp; // a turn/completed seen while _pending=Start (turn id unknown yet)
    bool      _dispatching;              // guards the fire-outside-lock dispatch against re-entrancy
    bool      _lastInFlight;             // last value handed to _onTurnInFlight, so it fires only on change

    public CodexTurnInputDispatcher(
            Func<string, CancellationToken, Task<CodexTurnStarted>> startTurn,
            Func<string, string, CancellationToken, Task> steerTurn,
            ILogger logger, CancellationToken ct, Action<bool>? onTurnInFlight = null) {
        _startTurn      = startTurn;
        _steerTurn      = steerTurn;
        _logger         = logger;
        _ct             = ct;
        _onTurnInFlight = onTurnInFlight;
    }

    /// <summary>True while a turn is live — the runtime's turn-in-flight signal.</summary>
    public bool TurnInFlight { get { lock (_gate) return _lifecycle != Lifecycle.Idle; } }

    /// <summary>The active turn id, or null when idle — the target for a <c>turn/interrupt</c>.</summary>
    public string? CurrentTurnId { get { lock (_gate) return _lifecycle == Lifecycle.Active ? _activeTurnId : null; } }

    /// <summary>Enqueues one input and returns a task that completes when the dispatch carrying it
    /// (a <c>turn/start</c> or an accepted <c>turn/steer</c>) succeeds — the "wait for write" contract.
    /// Faults if that dispatch errors or the runtime is torn down.</summary>
    public Task EnqueueAsync(string text) {
        var item = new InputItem(text);
        lock (_gate) _queue.Enqueue(item);
        PumpDispatch();
        return item.Ack.Task;
    }

    /// <summary>Completes when the dispatcher is fully settled — no turn active, nothing pending, queue
    /// drained. The runtime's <c>WaitForTurnIdleAsync</c> composes this with its terminal signal.</summary>
    public Task WaitForSettledAsync() {
        lock (_gate) {
            if (IsSettledLocked()) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _settledWaiters.Add(tcs);
            return tcs.Task;
        }
    }

    /// <summary>Fed from the <c>turn/completed</c> notification. Advances the lifecycle and, when a
    /// <c>turn/start</c> response has not yet identified its turn, stashes the completion so the
    /// response can reconcile "this input rode a turn that already finished".</summary>
    public void OnTurnCompleted(string? turnId) {
        lock (_gate) {
            if (_pending is Pending.Start && _activeTurnId is null) {
                // Completion raced ahead of the start response — remember it by id; the response reconciles.
                if (turnId is not null) _completedBeforeStartResp = turnId;
                return;
            }
            // A null turn id means "the active turn completed" (the notification omitted it) — accept it
            // for whatever is active; a non-null id that doesn't match the active turn is stale.
            if (turnId is not null && _activeTurnId is not null
                && !string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
                return;
            _lifecycle    = Lifecycle.Idle;
            _activeTurnId = null;
        }
        SignalTransitions();
        PumpDispatch();
    }

    /// <summary>Faults every queued and in-flight input — called on runtime teardown so no
    /// "wait for write" caller hangs.</summary>
    public void FaultAll(Exception ex) {
        List<InputItem> orphans;
        lock (_gate) {
            orphans = [.. _queue];
            _queue.Clear();
            _pending      = Pending.None;
            _lifecycle    = Lifecycle.Idle;
            _activeTurnId = null;
        }
        foreach (var item in orphans) item.Ack.TrySetException(ex);
        SignalTransitions();
    }

    // Drains the queue as far as the invariant allows. Only one thread runs the fire-outside-lock body
    // at a time (_dispatching); other callers just mark that another pass is needed and return.
    void PumpDispatch() {
        lock (_gate) {
            if (_dispatching) return;
            _dispatching = true;
        }

        _ = DispatchLoopAsync();
    }

    async Task DispatchLoopAsync() {
      try {
        while (true) {
            InputItem item;
            bool asSteer;
            string turnId;

            lock (_gate) {
                if (_pending is not Pending.None || _queue.Count == 0) {
                    _dispatching = false;
                    return;
                }
                item = _queue.Peek();
                if (_lifecycle == Lifecycle.Active && _activeTurnId is { } t) {
                    asSteer = true;
                    turnId  = t;
                    _pending = Pending.Steer;
                } else {
                    asSteer = false;
                    turnId  = "";
                    _pending = Pending.Start;
                    _completedBeforeStartResp = null;
                }
            }

            if (asSteer) await DispatchSteerAsync(item, turnId).ConfigureAwait(false);
            else         await DispatchStartAsync(item).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        // The dispatch primitives handle their own errors; this only fires on an unexpected fault, and
        // must reset the guard so input dispatch can never wedge on a stuck _dispatching flag.
        _logger.LogError(ex, "codex app-server: input dispatch loop faulted unexpectedly.");
        lock (_gate) _dispatching = false;
      }
    }

    async Task DispatchStartAsync(InputItem item) {
        try {
            var started = await _startTurn(item.Text, _ct).ConfigureAwait(false);
            lock (_gate) {
                _pending = Pending.None;
                var alreadyDone = started.Status is not null and not "inProgress"
                    || string.Equals(_completedBeforeStartResp, started.TurnId, StringComparison.Ordinal);
                _completedBeforeStartResp = null;
                if (alreadyDone) {
                    _lifecycle    = Lifecycle.Idle;
                    _activeTurnId = null;
                } else {
                    _lifecycle    = Lifecycle.Active;
                    _activeTurnId = started.TurnId;
                }
                _queue.Dequeue();
            }
            item.Ack.TrySetResult(); // the start carried this input (it was the turn's prompt)
        } catch (Exception ex) {
            FailHead(item, ex);
        }
        SignalTransitions();
    }

    async Task DispatchSteerAsync(InputItem item, string turnId) {
        try {
            await _steerTurn(turnId, item.Text, _ct).ConfigureAwait(false);
            lock (_gate) {
                _pending = Pending.None;
                _queue.Dequeue();
            }
            item.Ack.TrySetResult(); // accepted onto the active turn before it completed
        } catch (CodexAppServerRpcException rpc) when (rpc.Code == -32600 && !item.RetriedAsStart) {
            // The turn ended before the steer landed (spike Q13). Retry this same input EXACTLY ONCE
            // as a turn/start — force the lifecycle idle for the missed turn so the retry can't re-steer.
            lock (_gate) {
                item.RetriedAsStart = true;
                _pending = Pending.None;
                if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal)) {
                    _lifecycle    = Lifecycle.Idle;
                    _activeTurnId = null;
                }
            }
            _logger.LogDebug("codex app-server: steer missed turn {TurnId}; retrying the input as turn/start.", turnId);
        } catch (Exception ex) {
            FailHead(item, ex);
        }
        SignalTransitions();
    }

    void FailHead(InputItem item, Exception ex) {
        lock (_gate) {
            _pending = Pending.None;
            if (_queue.Count > 0 && ReferenceEquals(_queue.Peek(), item)) _queue.Dequeue();
        }
        item.Ack.TrySetException(ex);
    }

    bool IsSettledLocked() => _lifecycle == Lifecycle.Idle && _pending == Pending.None && _queue.Count == 0;

    // Fires the in-flight callback on a real change and releases any settled waiters — both OUTSIDE the
    // lock so a callback can never re-enter the dispatcher under it.
    void SignalTransitions() {
        bool inFlight;
        bool fireInFlight = false;
        List<TaskCompletionSource>? settled = null;

        lock (_gate) {
            inFlight = _lifecycle != Lifecycle.Idle;
            if (inFlight != _lastInFlight) { _lastInFlight = inFlight; fireInFlight = true; }
            if (IsSettledLocked() && _settledWaiters.Count > 0) {
                settled = [.. _settledWaiters];
                _settledWaiters.Clear();
            }
        }

        if (fireInFlight) _onTurnInFlight?.Invoke(inFlight);
        if (settled is not null) foreach (var w in settled) w.TrySetResult();
    }

    enum Pending   { None, Start, Steer }
    enum Lifecycle { Idle, Active }

    // An item is only ever touched by the single active dispatch loop (the _dispatching guard admits
    // exactly one), so its mutable RetriedAsStart needs no synchronization — the per-iteration lock
    // acquisitions fence the one write against the next iteration's read.
    sealed class InputItem(string text) {
        public string Text { get; } = text;
        public TaskCompletionSource Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RetriedAsStart { get; set; }
    }
}
