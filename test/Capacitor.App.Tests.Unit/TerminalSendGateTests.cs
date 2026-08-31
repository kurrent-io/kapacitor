using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The composer's send gate on TerminalTabViewModel: acceptance, the two-write delivery, and
/// every window in which a send must be refused or a pending CR dropped. Same RunOnUiAsync +
/// [NotInParallel("AvaloniaSession")] discipline as TerminalTabViewModelTests.
public class TerminalSendGateTests {
    static readonly byte[] Paste = TerminalInputEncoder.Paste("hello");
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    static async Task<(FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Factory, FakeTimeProvider Time, TerminalTabViewModel Vm, FakeTerminalAttachClient Client)>
            BuildAttachedAsync(string? readOnlyReason = null, Action<FakeTerminalAttachClient>? configureNext = null) {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory { ConfigureNext = configureNext };
        var time = new FakeTimeProvider();
        var vm = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
        await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([], readOnlyReason);
        return (daemon, factory, time, vm, client);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accepted_send_writes_the_paste_then_the_cr_after_the_delay_on_the_same_client() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();
            await Assert.That(vm.CanAcceptText).IsTrue();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Ready);

            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await Assert.That(vm.SendInFlight).IsTrue();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Sending);
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            await Assert.That(client.SentInput[0]).IsEquivalentTo(Paste);

            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;
            await Assert.That(client.SentInput).Count().IsEqualTo(2);
            await Assert.That(client.SentInput[1]).IsEquivalentTo(TerminalInputEncoder.Submit);
            await Assert.That(vm.SendInFlight).IsFalse();
            await Assert.That(vm.CanAcceptText).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_send_is_refused_while_one_is_in_flight_then_goes_through() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();

            await Assert.That(vm.TrySendText("A")).IsTrue();
            await Assert.That(vm.TrySendText("B")).IsFalse();
            await Assert.That(vm.CanAcceptText).IsFalse();

            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;
            await Assert.That(vm.TrySendText("B")).IsTrue();
            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;

            await Assert.That(client.SentInput.Select(Encoding.UTF8.GetString)).IsEquivalentTo(
                ["\x1b[200~A\x1b[201~", "\r", "\x1b[200~B\x1b[201~", "\r"], CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refused_unless_attached_read_write() {
        await RunOnUiAsync(async () => {
            var (_, _, _, ro, _) = await BuildAttachedAsync(readOnlyReason: "review");
            await Assert.That(ro.TrySendText("x")).IsFalse();
            await Assert.That(ro.SendAvailability).IsEqualTo(SendAvailability.ReadOnly);

            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var connecting = new TerminalTabViewModel("a2", daemon, factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a2", "claude", hasTerminal: true));
            await (connecting.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(connecting.TrySendText("x")).IsFalse();
            await Assert.That(connecting.SendAvailability).IsEqualTo(SendAvailability.Connecting);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_during_a_reattach_disposal_or_a_detach_write_is_refused_while_state_still_reads_attached() {
        await RunOnUiAsync(async () => {
            // Reattach: the old client's disposal is held open, State still Attached.
            var gate = new TaskCompletionSource();
            var (_, factory, _, vm, client) = await BuildAttachedAsync(configureNext: c => c.DisposeGate = gate);
            var reattach = vm.ReattachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client.DisposeCalls == 1, what: "old client disposing");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.CanAcceptText).IsFalse();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Transitioning);
            await Assert.That(vm.TrySendText("x")).IsFalse();

            // The fake terminalizes the run it retires (Detached) where the real client cancels
            // it, so draining that outcome here is what fixes the order the held-open reattach
            // resumes into, rather than letting the UI pump race the resumption.
            await vm.CurrentRunForTesting!;
            gate.SetResult();
            await reattach;
            await Assert.That(factory.Created.Count).IsEqualTo(1); // the drained outcome retired the attempt

            await vm.ReattachCommand.Execute();
            var client2 = factory.Created[^1];
            await Assert.That(vm.CanAcceptText).IsFalse(); // Connecting: still closed
            await client2.TriggerAttached([]);
            await Assert.That(vm.CanAcceptText).IsTrue();

            // Detach: the detach write is held open, State still Attached.
            var (_, _, _, vm2, client3) = await BuildAttachedAsync(configureNext: c => c.HangDetachForever = true);
            var detach = vm2.DetachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client3.DetachCalls == 1, what: "detach in flight");
            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm2.TrySendText("x")).IsFalse();
            await Assert.That(vm2.SendAvailability).IsEqualTo(SendAvailability.Transitioning);
            client3.DetachGate.SetResult();
            await detach;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Invalidation_during_the_delay_drops_the_cr_and_clears_in_flight() {
        await RunOnUiAsync(async () => {
            foreach (var invalidate in new Func<TerminalTabViewModel, FakeTerminalAttachClient, FakeDaemonClientService, Task>[] {
                async (vm, _, _) => { await vm.DetachCommand.Execute(); },
                async (vm, client, _) => { client.Result.SetResult(new AttachOutcome.Exited(0)); await vm.CurrentRunForTesting!; },
                async (vm, client, _) => { client.Result.SetResult(new AttachOutcome.ConnectionLost()); await vm.CurrentRunForTesting!; },
                (_, _, daemon) => { daemon.Agents.Remove("a1"); return Task.CompletedTask; },
                async (vm, _, _) => { await vm.TeardownAsync(); },
            }) {
                var (daemon, _, time, vm, client) = await BuildAttachedAsync();
                await Assert.That(vm.TrySendText("hello")).IsTrue();
                await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");

                await invalidate(vm, client, daemon);
                await Assert.That(vm.SendInFlight).IsFalse();
                await Assert.That(vm.CanAcceptText).IsFalse();
                await Assert.That(vm.TrySendText("again")).IsFalse();

                time.Advance(Delay);
                await (vm.PendingDeliveryForTesting ?? Task.CompletedTask);
                await Assert.That(client.SentInput).Count().IsEqualTo(1);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_attached_publish_cannot_reopen_after_a_removal_or_detach() {
        await RunOnUiAsync(async () => {
            // Removal while Connecting: the attach callback's publish is queued, then the agent goes.
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client = factory.Created.Single();
            var tokenWhileConnecting = vm.OpeningTokenForTesting;

            var attached = client.TriggerAttached([]);   // queued for the UI dispatch, not yet run
            daemon.Agents.Remove("a1");                  // lands first: SessionEnded, ownership advanced
            await attached;

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(vm.OpeningTokenForTesting).IsNotEqualTo(tokenWhileConnecting);
            await Assert.That(vm.CanAcceptText).IsFalse();
            await Assert.That(vm.TrySendText("x")).IsFalse();

            // An invalidation landing during a reattach's pre-Connecting disposal aborts that
            // attempt: no Connecting, no second client.
            var gate = new TaskCompletionSource();
            var (daemon2, factory2, _, vm2, client2) = await BuildAttachedAsync(configureNext: c => c.DisposeGate = gate);
            var reattach = vm2.ReattachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client2.DisposeCalls == 1, what: "old client disposing");
            // The fake terminalizes the run it retires (Detached) where the real client cancels it
            // -- the retiring Cancel claims the run and its outcome is swallowed as a retired
            // attempt's own cancellation. Draining that outcome here makes the removal below the
            // last publish, so what the abort leaves behind is asserted, not raced.
            await vm2.CurrentRunForTesting!;
            daemon2.Agents.Remove("a1");
            gate.SetResult();
            await reattach;

            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(factory2.Created.Count).IsEqualTo(1); // no second client was ever created

            // A detach landing while the attach callback's publish is still queued: the late
            // Attached is discarded on its stale token, so the gate never opens.
            var daemon3 = new FakeDaemonClientService();
            var factory3 = new FakeTerminalAttachClientFactory();
            var vm3 = new TerminalTabViewModel("a3", daemon3, factory3.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider());
            daemon3.Agents.AddOrUpdate(Agent("a3", "claude", hasTerminal: true));
            await (vm3.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client3 = factory3.Created.Single();

            var attached3 = client3.TriggerAttached([]);
            await vm3.DetachCommand.Execute();
            await attached3;

            await Assert.That(client3.DetachCalls).IsEqualTo(1);
            await Assert.That(vm3.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
            await Assert.That(vm3.CanAcceptText).IsFalse();
            await Assert.That(vm3.TrySendText("x")).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_write_fault_clears_in_flight_and_leaves_state_alone() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildAttachedAsync();
            client.ThrowOnSendInput = new IOException("pipe closed");

            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await vm.PendingDeliveryForTesting!;

            await Assert.That(vm.SendInFlight).IsFalse();
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.CanAcceptText).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_during_the_delay_queues_no_dispatcher_work_afterwards() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();
            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            var delivery = vm.PendingDeliveryForTesting!;

            await vm.TeardownAsync();
            var changes = 0;
            vm.PropertyChanged += (_, _) => changes++;
            time.Advance(Delay);
            await delivery;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(changes).IsEqualTo(0);
            await Assert.That(client.SentInput).Count().IsEqualTo(1);
        });
    }
}
