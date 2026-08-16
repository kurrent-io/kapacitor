using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Onboarding;

/// What the Connect step asked for; the composition root binds each case to one façade call.
public abstract record ConnectIntent {
    public sealed record Paste(string ServerInput) : ConnectIntent;
    public sealed record Discover(string Provider) : ConnectIntent;
    public sealed record Create : ConnectIntent;
}

/// <summary>
/// One in-flight sign-in. <see cref="Result"/> is the terminal answer the close path awaits and
/// never faults — an operation that throws arrives as <see cref="AuthResult.Failed"/> rather than
/// as an exception nobody is positioned to catch during shutdown.
/// </summary>
public sealed class AuthAttempt {
    readonly CancellationTokenSource _cts = new();

    internal AuthAttempt(Func<CancellationToken, Task<AuthResult>> run) => Result = RunAsync(run);

    public Task<AuthResult> Result { get; }

    /// Pre-boundary this ends the operation as Cancelled; past it the façade publishes and answers Committed.
    public void Cancel() {
        try {
            _cts.Cancel();
        } catch (ObjectDisposedException) {
            // already settled — nothing left to cancel
        }
    }

    async Task<AuthResult> RunAsync(Func<CancellationToken, Task<AuthResult>> run) {
        try {
            return await run(_cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
            return new AuthResult.Cancelled();
        } catch (Exception ex) {
            return new AuthResult.Failed(ex.Message);
        } finally {
            _cts.Dispose();
        }
    }
}

/// <summary>
/// The wizard's single-flight sign-in driver: one attempt at a time, cancellation as a distinct
/// outcome, and a terminal task the window's close path awaits before the app resolves
/// configuration. <paramref name="runOperation"/> binds the façade and its bridges, so this type
/// stays free of auth mechanics.
/// </summary>
public sealed class WizardAuthService(
        Func<ConnectIntent, CancellationToken, Task<AuthResult>> runOperation,
        ConsentFlipClaims                                        claims) {
    readonly Lock _gate = new();

    AuthAttempt? _current;

    /// The decision-7 hook the composition root hands each façade it builds for this service.
    public Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> BeforeCommit { get; } = ArmingHook(claims);

    public AuthAttempt? Current {
        get {
            lock (_gate) return _current;
        }
    }

    /// <summary>
    /// Arms one durable claim per identity before the boundary publishes anything. A false return
    /// is a store failure, not a cancel: it throws <see cref="InvalidOperationException"/>, because
    /// the boundary maps ANY <see cref="OperationCanceledException"/> from this hook to
    /// <see cref="AuthResult.Cancelled"/> — which would render a failed arm as "the user backed out"
    /// instead of a retryable error.
    /// </summary>
    public static Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> ArmingHook(ConsentFlipClaims claims) =>
        async (identities, _) => {
            foreach (var identity in identities) {
                // No token into Task.Run: arming is fast, and a cancel landing here must not abandon a half-written claim.
                var armed = await Task
                    .Run(() => claims.Arm(new ConsentFlipClaim(identity.Profile, identity.CanonicalServer)))
                    .ConfigureAwait(false);

                if (!armed) throw new InvalidOperationException("claim_arm_failed");
            }
        };

    /// <exception cref="InvalidOperationException">An attempt is still live — Retry re-runs only after quiesce.</exception>
    public AuthAttempt Begin(ConnectIntent intent) {
        lock (_gate) {
            if (_current is { Result.IsCompleted: false }) {
                throw new InvalidOperationException("A sign-in attempt is already running.");
            }

            return _current = new AuthAttempt(ct => runOperation(intent, ct));
        }
    }

    /// Completes when no attempt is live — the close path's await after Cancel (decision 2).
    public async Task QuiescedAsync() {
        while (true) {
            Task<AuthResult> live;

            lock (_gate) {
                if (_current is not { Result.IsCompleted: false } attempt) return;

                live = attempt.Result;
            }

            await live.ConfigureAwait(false);
        }
    }
}
