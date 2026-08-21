using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RegistrationGateTests {
    [Test]
    public async Task Not_ready_until_registered() {
        var gate = new RegistrationGate();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Connection_loss_clears_readiness_even_if_state_still_connected() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        gate.MarkUnregistered();

        // Models the Reconnected-before-RegisterDaemon window: state reports
        // Connected but we have not re-registered yet.
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();
    }

    [Test]
    public async Task Disconnected_states_are_never_ready() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Reconnecting)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Disconnected)).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Connecting)).IsFalse();
    }

    [Test]
    public async Task Re_registration_restores_readiness() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        gate.MarkUnregistered();

        gate.MarkRegistered();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task Slot_displacement_without_transport_loss_drops_readiness_until_reregistered() {
        var gate = new RegistrationGate();
        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();

        // Heartbeat sees PingAsync()==false and re-registers. RegisterDaemon()
        // brackets the call: MarkUnregistered() at start, MarkRegistered() on
        // success — the transport never dropped, so state stays Connected.
        gate.MarkUnregistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        gate.MarkRegistered();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    // RunRegistrationAsync brackets the FULL (re-)registration — DaemonConnect AND per-agent
    // re-registration — so IsReady can't flip true in the gap between them. That gap was the
    // residual silent-deny race: a permission invoke gated only on DaemonConnect could fire
    // before the server re-established session ownership and get a HubException → deny.
    [Test]
    public async Task RunRegistration_holds_readiness_until_agent_reregistration_completes() {
        var gate              = new RegistrationGate();
        var reRegisterStarted = new TaskCompletionSource();
        var releaseReRegister = new TaskCompletionSource();

        var run = gate.RunRegistrationAsync(
            daemonConnect: () => Task.CompletedTask,
            reRegisterAgents: async () => {
                reRegisterStarted.SetResult();
                await releaseReRegister.Task;
            });

        // DaemonConnect has completed; per-agent re-registration is in flight — NOT ready yet.
        await reRegisterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();

        releaseReRegister.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task RunRegistration_runs_daemon_connect_before_agent_reregistration() {
        var gate  = new RegistrationGate();
        var order = new List<string>();

        await gate.RunRegistrationAsync(
            daemonConnect: () => {
                order.Add("connect");

                return Task.CompletedTask;
            },
            reRegisterAgents: () => {
                order.Add("reregister");

                return Task.CompletedTask;
            });

        await Assert.That(order).IsEquivalentTo(new[] { "connect", "reregister" });
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    [Test]
    public async Task RunRegistration_daemon_connect_failure_skips_reregistration_and_stays_unready() {
        var gate         = new RegistrationGate();
        gate.MarkRegistered(); // pretend a prior connection had us ready
        var reRegistered = false;

        await Assert.That(async () => await gate.RunRegistrationAsync(
                    daemonConnect: () => throw new InvalidOperationException("boom"),
                    reRegisterAgents: () => {
                        reRegistered = true;

                        return Task.CompletedTask;
                    }))
            .Throws<InvalidOperationException>();

        await Assert.That(reRegistered).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsFalse();
    }

    // Settlement lost-ack redelivery (D1): postRegister runs AFTER MarkRegistered, so a throw there
    // (including from a throwing ILogger inside the hook's own error handling — a supported input) must
    // be contained: RunRegistrationAsync must not fault the reconnect path, and readiness stays set.
    [Test]
    public async Task A_throwing_post_register_hook_is_contained_and_readiness_stays_set() {
        var gate = new RegistrationGate();

        await gate.RunRegistrationAsync(
            daemonConnect:    () => Task.CompletedTask,
            reRegisterAgents: () => Task.CompletedTask,
            postRegister:     () => throw new InvalidOperationException("hook and its logger both blew up"));

        // The daemon is registered despite the post-register throw — the bracket completed cleanly.
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }

    // Cancellation is a shutdown signal, NOT a hook failure: it must propagate out of the bracket rather
    // than be swallowed as success (readiness still set, since MarkRegistered ran first).
    [Test]
    public async Task A_cancelled_post_register_hook_propagates_the_cancellation() {
        var gate = new RegistrationGate();

        await Assert.That(async () => await gate.RunRegistrationAsync(
                daemonConnect:    () => Task.CompletedTask,
                reRegisterAgents: () => Task.CompletedTask,
                postRegister:     () => throw new OperationCanceledException()))
            .Throws<OperationCanceledException>();

        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }
}
