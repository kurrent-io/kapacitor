using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Tracks whether the daemon's hub connection is ready to carry a daemon-scoped
/// invocation — i.e. the transport is <see cref="HubConnectionState.Connected"/>
/// AND <c>DaemonConnect</c> (re-registration) has completed on the current
/// connection. Used by the permission-request retry loop so a retry never fires
/// against a connection the server has not (re-)registered, which would surface
/// as a HubException and be mistaken for a final deny.
///
/// The flag is reset on every connection drop (Reconnecting/Closed) and at the
/// start of every re-registration (including the heartbeat slot-displacement
/// path, which re-registers without any transport-loss event), and set only
/// after a successful registration.
/// </summary>
internal sealed class RegistrationGate {
    volatile bool _registered;

    public void MarkUnregistered() => _registered = false;
    public void MarkRegistered()   => _registered = true;

    public bool IsReady(HubConnectionState state) =>
        state == HubConnectionState.Connected && _registered;

    /// <summary>
    /// Runs a full (re-)registration as one bracket so readiness is restored only after BOTH
    /// the daemon-level <c>DaemonConnect</c> AND per-agent re-registration have completed:
    /// MarkUnregistered → <paramref name="daemonConnect"/> → <paramref name="reRegisterAgents"/>
    /// → MarkRegistered.
    ///
    /// Without this, <see cref="IsReady"/> would flip true the instant <c>DaemonConnect</c>
    /// returned — before the server re-established per-session ownership via the separate agent
    /// re-registration path. A permission invoke gated on <see cref="IsReady"/> could then fire
    /// into that gap and get a "Caller is not the daemon owning session" HubException, which the
    /// retry layer treats as fatal → a spurious deny (the residual half of the bug).
    ///
    /// If <paramref name="daemonConnect"/> throws (e.g. name-in-use), the exception propagates
    /// and readiness stays cleared — re-registration is skipped and MarkRegistered never runs.
    /// </summary>
    public async Task RunRegistrationAsync(
            Func<Task> daemonConnect, Func<Task> reRegisterAgents, Func<Task>? postRegister = null) {
        MarkUnregistered();
        await daemonConnect();
        await reRegisterAgents();
        MarkRegistered();
        // postRegister runs with IsReady == true — for work that must reach the server on the freshly
        // ready transport (e.g. re-delivering unretired terminal acks, which CommandAckAsync drops while
        // readiness is still cleared inside this bracket). Its failure must not un-register the daemon,
        // so it is the caller's job to contain it.
        if (postRegister is not null) await postRegister();
    }
}
