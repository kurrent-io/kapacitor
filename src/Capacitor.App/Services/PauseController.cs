using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Serializes ALL consent-policy IPC — passive refreshes and user toggles alike — through one
/// lane, so results apply in start order and an older read can never overwrite a newer write's
/// outcome (spec §6). ONE lock guards every lane/slot/state transition; the IPC calls
/// themselves run unlocked on the thread pool from the void request methods, which never throw.
public sealed class PauseController : IPauseController, IDisposable {
    static readonly ConsentRuleDto PauseRule = new("deny", null, null, null, null);

    const string DaemonUnreachableReason = "daemon_unreachable";
    const string UnreachableCopy         = "The daemon is not reachable";
    const string RejectedNeutralCopy     = "The daemon rejected the change";

    enum Lane { Idle, Passive, Toggle }

    readonly ILocalControlOps _ops;
    readonly Action<string> _notify;
    readonly CancellationToken _shutdownToken;
    readonly BehaviorSubject<PauseState> _state = new(new PauseState(false, false, false));

    readonly Lock _lock = new();
    Lane _lane;
    bool? _queuedDesired; // one-slot queue, reserved exclusively for a toggle that arrives while a passive owns the lane
    bool _checked;
    bool _verified;
    bool _disposed;

    public PauseController(ILocalControlOps ops, Action<string> notify, CancellationToken shutdownToken) {
        _ops = ops;
        _notify = notify;
        _shutdownToken = shutdownToken;
    }

    public IObservable<PauseState> State => _state.AsObservable();

    public void RequestRefresh() {
        lock (_lock) {
            if (_disposed || _lane != Lane.Idle) return; // busy lane: dropped silently, no push (spec §6)
            _lane = Lane.Passive;
        }
        _ = Task.Run(RunPassiveAsync);
    }

    public void RequestToggle(bool desired) {
        bool startNow;
        lock (_lock) {
            if (_disposed) return;
            if (_lane == Lane.Toggle) return; // a toggle owns the lane (running): ignored, single-flight
            if (_lane == Lane.Passive) {
                if (_queuedDesired.HasValue) return; // slot already reserved: ignored (toggle "owns" it too)
                _queuedDesired = desired;             // runs once the passive completes, success or failure
                startNow = false;
            } else {
                _lane = Lane.Toggle;
                startNow = true;
            }
            PushLocked(); // Busy becomes true immediately, whether queued or started now
        }
        if (startNow) _ = Task.Run(() => RunToggleAsync(desired));
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _state.OnCompleted();
        _state.Dispose();
    }

    async Task RunPassiveAsync() {
        try {
            var policy = await _ops.GetConsentPolicyAsync(_shutdownToken).ConfigureAwait(false);
            FinishPassive(success: true, checkedValue: HasPauseRuleAtZero(policy));
        } catch (OperationCanceledException) {
            ResetQuietly();
        } catch (LocalControlOpsException) {
            Console.Error.WriteLine("kcap: launch-pause refresh failed");
            FinishPassive(success: false, checkedValue: false);
        } catch (Exception ex) {
            // Never leak the lane: an unmapped exception (e.g. ArgumentOutOfRangeException from
            // an over-long UnixDomainSocketEndPoint path — LocalControlOps.ExchangeAsync does not
            // classify every socket-construction failure) must still release it, or every later
            // RequestRefresh/RequestToggle is silently dropped/ignored forever.
            Console.Error.WriteLine($"kcap: launch-pause refresh failed unexpectedly: {ex.Message}");
            FinishPassive(success: false, checkedValue: false);
        }
    }

    // Fresh Get -> apply toward the desired state (idempotent no-op when it already holds) ->
    // ack handling -> trailing refresh. Runs even when it was queued behind a passive op that
    // itself failed — this Get is its own independent read, never a reuse of the passive's.
    async Task RunToggleAsync(bool desired) {
        try {
            var policy = await _ops.GetConsentPolicyAsync(_shutdownToken).ConfigureAwait(false);
            var hasRule = HasPauseRuleAtZero(policy);
            if (desired != hasRule) {
                var rules = new List<ConsentRuleDto>(policy.Rules);
                if (desired) rules.Insert(0, PauseRule); else rules.RemoveAt(0);
                var ack = await _ops
                    .PutConsentPolicyAsync(new ConsentPolicyDto(policy.Default, policy.PromptTimeoutSeconds, rules), _shutdownToken)
                    .ConfigureAwait(false);
                HandleAck(ack);
            }
        } catch (OperationCanceledException) {
            ResetQuietly();
            return; // cancelled: no trailing refresh, no push
        } catch (LocalControlOpsException ex) {
            _notify(MapReason(ex)); // still attempt the trailing refresh below — it alone decides Verified
        } catch (Exception ex) {
            // Never leak the lane (see RunPassiveAsync) — no mapped copy exists for an unmapped
            // exception, so this logs only, same as the OCE-quiet path's spirit but still
            // reaching the trailing refresh below (unlike OCE, which is a deliberate shutdown).
            Console.Error.WriteLine($"kcap: launch-pause toggle failed unexpectedly: {ex.Message}");
        }

        await RunTrailingRefreshAsync().ConfigureAwait(false);
    }

    async Task RunTrailingRefreshAsync() {
        try {
            var policy = await _ops.GetConsentPolicyAsync(_shutdownToken).ConfigureAwait(false);
            CompleteToggle(success: true, checkedValue: HasPauseRuleAtZero(policy));
        } catch (OperationCanceledException) {
            ResetQuietly();
        } catch (LocalControlOpsException) {
            Console.Error.WriteLine("kcap: launch-pause trailing refresh failed");
            CompleteToggle(success: false, checkedValue: false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: launch-pause trailing refresh failed unexpectedly: {ex.Message}");
            CompleteToggle(success: false, checkedValue: false);
        }
    }

    void HandleAck(ConsentAckDto ack) {
        if (!ack.Ok) _notify(string.IsNullOrEmpty(ack.Error) ? RejectedNeutralCopy : ack.Error);
        else if (!string.IsNullOrEmpty(ack.Error)) Console.Error.WriteLine($"kcap: launch-pause change applied with a warning: {ack.Error}");
    }

    void FinishPassive(bool success, bool checkedValue) {
        bool startToggle = false;
        var toggleDesired = false;
        lock (_lock) {
            if (_disposed) return;
            if (success) { _checked = checkedValue; _verified = true; } else _verified = false;

            if (_queuedDesired.HasValue) {
                toggleDesired = _queuedDesired.Value;
                _queuedDesired = null;
                _lane = Lane.Toggle; // hand the lane straight to the queued toggle, never back to Idle
                startToggle = true;
            } else {
                _lane = Lane.Idle;
            }
            PushLocked();
        }
        if (startToggle) _ = Task.Run(() => RunToggleAsync(toggleDesired));
    }

    void CompleteToggle(bool success, bool checkedValue) {
        lock (_lock) {
            if (_disposed) return;
            if (success) { _checked = checkedValue; _verified = true; } else _verified = false;
            _lane = Lane.Idle; // a toggle never leaves a queued follow-up — the slot is passive-only
            PushLocked();
        }
    }

    // OperationCanceledException (shutdown token) anywhere is absorbed quietly: no notify, no
    // stderr, no Verified change, no state push — just clear the lane and the queued slot so a
    // later RequestRefresh/RequestToggle is not dropped forever.
    void ResetQuietly() {
        lock (_lock) {
            if (_disposed) return;
            _lane = Lane.Idle;
            _queuedDesired = null;
        }
    }

    // Caller must hold _lock. Busy is toggle-running OR toggle-queued; a passive-only lane
    // occupancy never counts (spec §6 — the item stays enabled and a click queues).
    // OnNext under _lock is deliberate, not incidental: it is what makes OnNext-after-Dispose
    // impossible (Dispose also sets _disposed under _lock) — do not move it outside the lock.
    void PushLocked() => _state.OnNext(new PauseState(_checked, _verified, _lane == Lane.Toggle || _queuedDesired.HasValue));

    static bool HasPauseRuleAtZero(ConsentPolicyDto policy) =>
        policy.Rules.Count > 0 && policy.Rules[0] is { Action: "deny", Requester: null, Kind: null, Repo: null, Vendor: null };

    static string MapReason(LocalControlOpsException ex) =>
        ex.Reason == DaemonUnreachableReason ? UnreachableCopy : $"Couldn't update launch pause: {ex.Message}";
}
