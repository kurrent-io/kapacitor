namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Retries a hub invocation across transient connection drops. Used by
/// <see cref="ServerConnection.RequestPermissionAsync"/> so a SignalR blip
/// during a (potentially hours-long) permission wait doesn't surface to the
/// caller as a failure — and therefore doesn't get turned into a silent deny.
///
/// Every attempt — including the first — waits until the connection is ready
/// (Connected AND re-registered) before invoking, so a request that arrives
/// mid-reconnect can't fire against a connection the server hasn't registered.
/// Because of that gating, a failure is a transient disconnect when it is an
/// <see cref="OperationCanceledException"/> (SignalR cancels in-flight
/// invocations when the transport drops) or an
/// <see cref="InvalidOperationException"/> ("connection is not active" — the
/// transport went down in the gap between the readiness check and the call).
/// Both are retried. Every other exception — <c>HubException</c> (server
/// rejected) or anything unrecognized — propagates to the caller (→ the bridge
/// denies).
///
/// The transient-disconnect loop is bounded only by <c>ct</c> (daemon shutdown). The
/// caller's shutdown token is excluded from the transient classification, so a
/// shutdown <see cref="OperationCanceledException"/> propagates rather than
/// being retried.
///
/// A caller may additionally supply <c>isRetriableServerError</c> +
/// <c>maxServerErrorRetries</c> to retry a BOUNDED number of times on specific
/// server-rejection exceptions (the "Caller is not the daemon owning session"
/// <c>HubException</c> that can appear briefly after a reconnect, before per-agent
/// re-registration has restored ownership — retrying past that window avoids a spurious deny).
/// Unlike transient disconnects, these retries are capped so a genuinely-permanent server error
/// still surfaces (→ deny) instead of looping forever.
/// </summary>
internal static class ConnectionRetry {
    /// <summary>
    /// Non-generic overload for a hub invocation with no return value (e.g. <c>AcpSessionStarted</c>)
    /// — delegates to the generic overload with a discarded
    /// <see langword="object?"/> result so void-returning callers get the exact same gating/retry
    /// semantics as <see cref="InvokeWithConnectionRetryAsync{T}"/> without duplicating the loop.
    /// </summary>
    public static Task InvokeWithConnectionRetryAsync(
            Func<Task>             invoke,
            Func<bool>             isReady,
            TimeSpan               pollInterval,
            Action<int>            onRetry,
            CancellationToken      ct,
            Func<Exception, bool>? isRetriableServerError = null,
            int                    maxServerErrorRetries = 0
        ) => InvokeWithConnectionRetryAsync<object?>(
            async () => { await invoke(); return null; },
            isReady,
            pollInterval,
            onRetry,
            ct,
            isRetriableServerError,
            maxServerErrorRetries
        );

    public static async Task<T> InvokeWithConnectionRetryAsync<T>(
            Func<Task<T>>      invoke,
            Func<bool>         isReady,
            TimeSpan           pollInterval,
            Action<int>        onRetry,
            CancellationToken  ct,
            Func<Exception, bool>? isRetriableServerError = null,
            int                maxServerErrorRetries = 0
        ) {
        var serverErrorRetries = 0;

        for (var attempt = 1; ; attempt++) {
            // Wait for readiness before EVERY attempt, including the first. A
            // permission request can arrive while the daemon is mid-reconnect or
            // re-registering; invoking then could hit a connection the server
            // hasn't registered and surface as a HubException — a spurious deny.
            // Gating all attempts on readiness (not just post-failure retries)
            // closes that race.
            while (!ct.IsCancellationRequested && !isReady())
                await Task.Delay(pollInterval, ct);

            ct.ThrowIfCancellationRequested();

            try {
                return await invoke();
            } catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDisconnect(ex)) {
                onRetry(attempt);

                // Brief delay before looping back to the readiness wait, so the
                // loop can never spin hot even if isReady() flips true instantly.
                await Task.Delay(pollInterval, ct);
            } catch (Exception ex) when (!ct.IsCancellationRequested
                                      && isRetriableServerError is not null
                                      && serverErrorRetries < maxServerErrorRetries
                                      && isRetriableServerError(ex)) {
                serverErrorRetries++;
                onRetry(attempt);
                await Task.Delay(pollInterval, ct);
            }
        }
    }

    // We only ever invoke once the connection was observed ready, so an
    // InvalidOperationException ("connection is not active") here means the
    // transport dropped between the readiness check and the call — transient,
    // like the OperationCanceledException SignalR raises for an in-flight
    // invocation killed by a transport loss. HubException and any other
    // exception are not transient and propagate.
    static bool IsTransientDisconnect(Exception ex) =>
        ex is OperationCanceledException or InvalidOperationException;
}
