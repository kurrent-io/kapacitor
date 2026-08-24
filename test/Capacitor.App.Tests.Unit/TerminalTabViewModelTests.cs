using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// TerminalTabViewModel's resolve gate, attempt lifecycle and outcome mapping. Every test here
/// (except the scheduler-assertion test) runs through RunOnUiAsync and carries
/// [NotInParallel("AvaloniaSession")]. Two DISTINCT things are needed together, not either alone:
/// the resolve gate's Agents-cache projection ObserveOn(RxSchedulers.MainThreadScheduler) needs
/// WithImmediateRxScheduler (same reason as HomeViewModelTests' identical header comment) to apply
/// synchronously; but the attempt lifecycle's Dispatcher.UIThread.InvokeAsync hops (the swap,
/// OnAttached/OnOutput, outcome mapping) need a LIVE dispatcher loop to ever be serviced at all --
/// HeadlessUnitTestSession's own doc comment: "All UI tests are supposed to be executed from one
/// of the Dispatch(...) methods to keep execution flow on the UI thread" -- outside an active
/// Dispatch(...) frame its worker thread is simply blocked waiting for the next one, so an
/// InvokeAsync queued from a bare WithImmediateRxScheduler body (no Dispatch involved) NEVER runs
/// and the awaiting call hangs forever (confirmed empirically). ActivityViewModelTests' own header
/// comment documents the identical fix for its Dispatcher.UIThread.InvokeAsync hop. RunOnUiAsync
/// nests WithImmediateRxScheduler INSIDE DispatchAsync so both are satisfied at once.
public class TerminalTabViewModelTests {
    sealed class FakeTerminalSurface : ITerminalSurface {
        public List<string> Fed { get; } = [];
        public void Feed(string text) => Fed.Add(text);
        public event Action<byte[]>? InputProduced;
        public event Action<int, int>? Resized;
        public void RaiseInput(byte[] bytes) => InputProduced?.Invoke(bytes);
        public void RaiseResize(int cols, int rows) => Resized?.Invoke(cols, rows);
    }

    static AgentStatusDto Agent(string id, string vendor, bool? hasTerminal, string? repoPath = null) => new(
        id, "agent", vendor, repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, HasTerminal: hasTerminal);

    static TerminalTabViewModel Build(
            FakeDaemonClientService daemon, FakeTerminalAttachClientFactory factory, FakeTimeProvider time,
            string agentId = "a1", Func<ITerminalSurface>? surfaceFactory = null) =>
        new(agentId, daemon, factory.Factory, surfaceFactory ?? (() => new FakeTerminalSurface()), time);

    // Real-time poll for a condition an async continuation settles OUTSIDE the test's own await
    // chain (e.g. a Task.ContinueWith observer attached to an abandoned task) -- never used to
    // gate FakeTimeProvider-driven logic itself, only to let its already-fired continuations
    // flush. Same idiom as ConsentServiceTests/PauseControllerTests etc.
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    // See the class doc comment: WithImmediateRxScheduler alone never pumps Dispatcher.UIThread,
    // so a test that reaches TryStartAttemptAsync/OnAttached/OnOutput (any Dispatcher.UIThread.
    // InvokeAsync hop) needs the real, live dispatcher loop DispatchAsync provides. Nested rather
    // than either alone.
    static Task RunOnUiAsync(Func<Task> body) =>
        AvaloniaSession.DispatchAsync(async () => {
            await AvaloniaSession.WithImmediateRxScheduler(body);
            return true;
        });

    static async Task<(FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Factory, FakeTimeProvider Time, TerminalTabViewModel Vm, FakeTerminalAttachClient Client)>
            BuildConnectingAsync(string vendor = "claude", bool? hasTerminal = true, string agentId = "a1", Action<FakeTerminalAttachClient>? configureNext = null) {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory { ConfigureNext = configureNext };
        var time = new FakeTimeProvider();
        var vm = Build(daemon, factory, time, agentId);

        daemon.Agents.AddOrUpdate(Agent(agentId, vendor, hasTerminal));
        await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);

        return (daemon, factory, time, vm, factory.Created.Single());
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opens_resolving_and_no_client_exists_before_the_first_dto() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var time = new FakeTimeProvider();
            var vm = Build(daemon, factory, time, agentId: "missing");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resolving);
            await Assert.That(factory.Created.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Has_terminal_false_renders_the_note_with_zero_attempts() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var time = new FakeTimeProvider();
            var vm = Build(daemon, factory, time, agentId: "a1");

            daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.NoTerminal);
            await Assert.That(vm.State.Detail).Contains("runs over ACP");
            await Assert.That(factory.Created.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Has_terminal_true_and_null_pty_fallback_proceed_to_attach() {
        await RunOnUiAsync(async () => {
            // Case 1: explicit hasTerminal:true, vendor gemini (whose vendor family is acp).
            var daemon1 = new FakeDaemonClientService();
            var factory1 = new FakeTerminalAttachClientFactory();
            var vm1 = Build(daemon1, factory1, new FakeTimeProvider(), agentId: "a1");

            daemon1.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: true));
            await (vm1.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(factory1.Created.Count).IsEqualTo(1);
            await Assert.That(vm1.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);

            // Case 2: hasTerminal:null (older daemon), vendor claude falls back to its pty family.
            var daemon2 = new FakeDaemonClientService();
            var factory2 = new FakeTerminalAttachClientFactory();
            var vm2 = Build(daemon2, factory2, new FakeTimeProvider(), agentId: "a2");

            daemon2.Agents.AddOrUpdate(Agent("a2", "claude", hasTerminal: null));
            await (vm2.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(factory2.Created.Count).IsEqualTo(1);
            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Resolve_timeout_is_not_found_and_a_late_dto_is_ignored_until_retry() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var time = new FakeTimeProvider();
            var vm = Build(daemon, factory, time, agentId: "a1");

            time.Advance(TimeSpan.FromSeconds(10));
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.NotFound);

            // A late DTO reaches nothing -- the timeout disposed the Agents subscription.
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.NotFound);
            await Assert.That(factory.Created.Count).IsEqualTo(0);

            // RetryResolveCommand resolves against the now-present DTO.
            await vm.RetryResolveCommand.Execute();

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
            await Assert.That(factory.Created.Count).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Removal_after_first_observation_is_session_ended_not_resolving() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var time = new FakeTimeProvider();
            var vm = Build(daemon, factory, time, agentId: "a1");

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);

            daemon.Agents.Remove("a1");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
        });
    }

    // ---- outcome mapping and attempt lifecycle ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Read_only_attach_shows_the_reason_and_suppresses_input_and_resize() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildConnectingAsync();

            await client.TriggerAttached([], reason: "review");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.State.ReadOnly).IsTrue();

            var surface = (FakeTerminalSurface)vm.Surface!;
            surface.RaiseInput([1, 2, 3]);
            surface.RaiseResize(100, 40);

            await Assert.That(client.SentInput).IsEmpty();
            await Assert.That(client.Resizes).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Exited_maps_to_exited_banner_and_connection_lost_maps_to_failed() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm1, client1) = await BuildConnectingAsync(agentId: "a1");
            client1.Result.SetResult(new AttachOutcome.Exited(3));
            await vm1.CurrentRunForTesting!;

            await Assert.That(vm1.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(vm1.State.ExitCode).IsEqualTo(3);

            var (_, _, _, vm2, client2) = await BuildConnectingAsync(agentId: "a2");
            client2.Result.SetResult(new AttachOutcome.ConnectionLost());
            await vm2.CurrentRunForTesting!;

            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(vm2.State.Detail).Contains("lost connection");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_background_run_fault_renders_failed_with_reattach() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildConnectingAsync();

            await Task.Run(() => client.Result.SetException(new AttachCallbackException(new InvalidOperationException("boom"))));
            await vm.CurrentRunForTesting!;

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(vm.State.Detail).IsEqualTo("boom");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Explicit_detach_stays_in_place_with_single_flight_reattach() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, client) = await BuildConnectingAsync();

            await vm.DetachCommand.Execute();
            await Assert.That(client.DetachCalls).IsEqualTo(1);

            client.Result.SetResult(new AttachOutcome.Detached());
            await vm.CurrentRunForTesting!;
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Detached);

            var before = factory.Created.Count;
            // Issued back-to-back with no await between them: ReactiveCommand.Execute() invokes
            // its Func<Task> eagerly and unconditionally (IL-verified, see the VM's class doc
            // comment), so both calls genuinely race the try-entered _attachLane.
            var t1 = vm.ReattachCommand.Execute().ToTask();
            var t2 = vm.ReattachCommand.Execute().ToTask();
            await Task.WhenAll(t1, t2);

            await Assert.That(factory.Created.Count).IsEqualTo(before + 1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reattach_swaps_in_a_fresh_surface_and_decoder_before_the_snapshot() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, client1) = await BuildConnectingAsync();
            var surface1 = vm.Surface;

            await client1.TriggerOutput("AB"u8.ToArray());
            client1.Result.SetResult(new AttachOutcome.ConnectionLost());
            await vm.CurrentRunForTesting!;
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);

            await vm.ReattachCommand.Execute();
            var client2 = factory.Created[^1];
            await Assert.That(client2).IsNotSameReferenceAs(client1);

            await client2.TriggerAttached("AB"u8.ToArray());

            await Assert.That(vm.Surface).IsNotSameReferenceAs(surface1);
            var surface2 = (FakeTerminalSurface)vm.Surface!;
            await Assert.That(surface2.Fed.Count(t => t == "AB")).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Exited_is_not_overwritten_by_session_ended() {
        await RunOnUiAsync(async () => {
            var (daemon, _, _, vm, client) = await BuildConnectingAsync();

            client.Result.SetResult(new AttachOutcome.Exited(0));
            await vm.CurrentRunForTesting!;
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);

            daemon.Agents.Remove("a1");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retired_attempts_cancellation_mutates_nothing() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, client1) = await BuildConnectingAsync();
            var run1 = vm.CurrentRunForTesting!;

            await vm.ReattachCommand.Execute(); // retires attempt 1: cancels + await-disposes client1

            // TrySetException, not SetException: DisposeAsync's own Result.TrySetResult(Detached)
            // (I4's fake-fidelity fix) may already have claimed Result as part of that retire --
            // this call's outcome doesn't matter either way, only that attempt 1 settles silently.
            client1.Result.TrySetException(new OperationCanceledException());
            await run1; // attempt 1's own run settles silently

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
            await Assert.That(factory.Created.Count).IsEqualTo(2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Utf8_split_across_snapshot_and_frames_renders_whole_characters() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildConnectingAsync();
            var euro = "€"u8.ToArray(); // E2 82 AC

            await client.TriggerAttached(euro[..1]);
            await client.TriggerOutput(euro[1..]);

            var surface = (FakeTerminalSurface)vm.Surface!;
            await Assert.That(string.Concat(surface.Fed)).IsEqualTo("€");
        });
    }

    /// Deliberately NOT wrapped in WithImmediateRxScheduler -- this test's whole point is thread
    /// IDENTITY (HomeViewSmokeTests' An_agent_arriving_off_the_UI_thread idiom), which Immediate
    /// would make a no-op by collapsing every hop onto the calling thread.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Surface_swap_and_state_mutations_happen_on_the_ui_thread() {
        var (surfaceCreatedOnUi, stateChangedOnUi, failure) = await AvaloniaSession.DispatchAsync(async () => {
            var (daemon, factory, time, vm, _) = await BuildConnectingAsync();
            Dispatcher.UIThread.RunJobs();

            bool? surfaceOnUi = null;
            bool? stateOnUi = null;
            var vm2 = new TerminalTabViewModel(
                "a2", daemon, factory.Factory,
                () => { surfaceOnUi ??= Dispatcher.UIThread.CheckAccess(); return new FakeTerminalSurface(); },
                time);
            vm2.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(TerminalTabViewModel.State))
                    stateOnUi ??= Dispatcher.UIThread.CheckAccess();
            };

            Exception? thrown = null;
            try {
                // Off-thread, exactly like the resolve gate itself is fed in production: the
                // Agents cache is mutated on the daemon client's own background pump.
                await Task.Run(() => daemon.Agents.AddOrUpdate(new AgentStatusDto(
                    "a2", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null, null)));

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (vm2.State.Phase != TerminalSessionPhase.Connecting && DateTime.UtcNow < deadline) {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }
            } catch (Exception ex) {
                thrown = ex;
            }

            Dispatcher.UIThread.RunJobs();
            return (surfaceOnUi, stateOnUi, thrown?.ToString());
        });

        await Assert.That(failure).IsNull();
        await Assert.That(surfaceCreatedOnUi).IsTrue();
        await Assert.That(stateChangedOnUi).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancel_dispose_orderings_each_yield_one_recorded_state() {
        await RunOnUiAsync(async () => {
            // Three interleavings of "the retired attempt's own outcome" racing "reattach's
            // retire step" (immediate completion, yielded completion, and a bare TrySetResult
            // fired from its own background Task) -- regardless of ordering, the VM settles on
            // exactly one coherent State (attempt 2's Connecting; the generation check makes
            // attempt 1's outcome a no-op whichever side of the retiring increment it lands on)
            // and the client is disposed exactly once.
            for (var i = 0; i < 3; i++) {
                var (_, factory, _, vm, client1) = await BuildConnectingAsync(agentId: $"race{i}");
                var run1 = vm.CurrentRunForTesting!;

                var reattach = Task.Run(() => vm.ReattachCommand.Execute().ToTask());
                var raceOther = i switch {
                    0 => Task.Run(() => client1.Result.TrySetResult(new AttachOutcome.Exited(1))),
                    1 => Task.Run(async () => {
                        await Task.Yield();
                        client1.Result.TrySetResult(new AttachOutcome.Exited(1));
                    }),
                    _ => Task.Run(() => client1.Result.TrySetResult(new AttachOutcome.Exited(1))),
                };
                await Task.WhenAll(reattach, raceOther, run1);

                await Assert.That(client1.DisposeCalls).IsEqualTo(1);
                await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
                await Assert.That(factory.Created.Count).IsEqualTo(2);
            }
        });
    }

    /// ConsoleOutput capture is process-global (its own doc comment), so this carries a BARE
    /// [NotInParallel] instead of the keyed AvaloniaSession one -- bare is the strictly stronger
    /// exclusion and still keeps this test out of every AvaloniaSession-tagged test's way.
    [Test]
    [NotInParallel]
    public async Task A_never_completing_detach_write_is_force_closed_within_the_bound() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildConnectingAsync(configureNext: c => c.HangDetachForever = true);

            using var stderr = ConsoleOutput.StartErrorCapture();

            var teardown = vm.TeardownAsync();

            time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => client.DisposeCalls == 1, what: "DisposeAsync called at the 1s detach bound");

            // A safety margin, not strictly required any more: DisposeAsync's own
            // Result.TrySetResult(Detached) (I4) settles the run step through ordinary Task
            // scheduling once DisposeCalls confirms it ran, but this covers a slow CI box too.
            time.Advance(TimeSpan.FromSeconds(2));
            await teardown;

            await Assert.That(client.DetachCalls).IsEqualTo(1);
            await Assert.That(client.DisposeCalls).IsEqualTo(1);

            var stateBefore = vm.State;
            client.DetachGate.SetException(new InvalidOperationException("boom"));
            await WaitUntilAsync(() => stderr.GetCapturedError().Length > 0, what: "abandoned detach diagnostic");

            await Assert.That(stderr.GetCapturedError()).Contains("boom");
            await Assert.That(vm.State).IsEqualTo(stateBefore);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_never_completing_awaited_callback_is_released_by_run_token_cancellation() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildConnectingAsync(configureNext: c => c.HangOnOutputForever = true);
            await client.RunStarted.Task;

            var surfaceRef = new WeakReference(vm.Surface);

            await vm.TeardownAsync();

            await Assert.That(client.CallbackTask!.IsCanceled).IsTrue();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Assert.That(surfaceRef.IsAlive).IsFalse();
        });
    }

    // ---- review fixes: C1/I1-I6 ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reattach_after_teardown_creates_no_client() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, _) = await BuildConnectingAsync();

            await vm.TeardownAsync();

            var before = factory.Created.Count;
            await vm.ReattachCommand.Execute();

            // Disposal wins permanently: a post-teardown Reattach must build nothing -- the
            // straggler client C1 reproduced (nothing would ever dispose it; TeardownAsync is
            // idempotent) never gets created at all.
            await Assert.That(factory.Created.Count).IsEqualTo(before);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_racing_a_straddling_reattach_neither_throws_nor_leaks_a_client() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, client1) = await BuildConnectingAsync();

            // Gate client1's DisposeAsync open so Reattach's retire step is still suspended
            // (straddling the swap) when TeardownAsync lands -- the class of race C1 reproduced:
            // a resumed cts.Token read/Register throwing ObjectDisposedException, and a second
            // live client nothing could ever dispose.
            client1.DisposeGate = new TaskCompletionSource();

            var reattach = Task.Run(() => vm.ReattachCommand.Execute().ToTask());
            await WaitUntilAsync(() => client1.DisposeCalls > 0, what: "reattach to start retiring client1");

            // Called directly, NOT via Task.Run: an async method's synchronous prefix always runs
            // on the calling thread up to its own first suspension point, so by the time this
            // call RETURNS (a pending Task), TeardownAsync has already set _resolveState =
            // Disposed and cancelled the attempt's cts -- deterministically, before the gate
            // below is released (both TeardownAsync's own client-handling and Reattach's retire
            // step independently captured the same `_client` = client1 before either nulled it,
            // so both may end up awaiting this same gate too; either way the ordering that
            // matters -- Disposed set before Reattach's re-check ever runs -- is guaranteed).
            var teardownTask = vm.TeardownAsync();

            client1.DisposeGate.SetResult();

            Exception? thrown = null;
            try { await teardownTask; } catch (Exception ex) { thrown = ex; }
            await reattach;

            await Assert.That(thrown).IsNull();
            // TeardownAsync's own resolveState re-check caught the race before Reattach ever
            // reached the factory call -- no second client. client1 was disposed (never left
            // live) -- possibly by both racing callers, since the real client's DisposeAsync is
            // itself idempotent for exactly that.
            await Assert.That(factory.Created.Count).IsEqualTo(1);
            await Assert.That(client1.DisposeCalls).IsGreaterThanOrEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retry_is_a_no_op_unless_the_gate_is_timed_out() {
        await RunOnUiAsync(async () => {
            var (_, factory, _, vm, _) = await BuildConnectingAsync(); // resolves normally -> dto-won, Connecting

            await vm.RetryResolveCommand.Execute();

            // The CAS only accepts a transition FROM timeout-won: a gate already resolved via a
            // normal DTO must never layer a second attach attempt on top of one that succeeded
            // (the old read-then-write let this through).
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
            await Assert.That(factory.Created.Count).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_never_completing_dispose_is_abandoned_within_the_teardown_budget() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildConnectingAsync(configureNext: c => c.DisposeGate = new TaskCompletionSource());

            var teardown = vm.TeardownAsync();
            await WaitUntilAsync(() => client.DisposeCalls == 1, what: "DisposeAsync entered");

            // DisposeAsync itself never completes on its own (the gate is never released) --
            // mirrors the real client, whose DisposeAsync awaits its own pump to fully unwind.
            // The remainder of the 3s budget must still force TeardownAsync to return.
            time.Advance(TimeSpan.FromSeconds(3));
            await teardown;

            await Assert.That(client.DisposeCalls).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_throwing_surface_factory_renders_failed_instead_of_hanging_in_resolving() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = new TerminalTabViewModel(
                "a1", daemon, factory.Factory,
                () => throw new InvalidOperationException("boom"),
                new FakeTimeProvider());

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);

            // A throw inside the swap must not leave the tab stuck in Resolving with an
            // unobserved task fault -- it renders as a local Failed instead.
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(factory.Created.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_trailing_partial_code_point_is_flushed_to_the_surface_on_exit() {
        await RunOnUiAsync(async () => {
            var (_, _, _, vm, client) = await BuildConnectingAsync();
            var euro = "€"u8.ToArray(); // E2 82 AC

            await client.TriggerAttached([]);
            await client.TriggerOutput(euro[..2]); // genuinely incomplete at exit -- missing the last byte

            client.Result.SetResult(new AttachOutcome.Exited(0));
            await vm.CurrentRunForTesting!;

            var surface = (FakeTerminalSurface)vm.Surface!;
            // The decoder's own carry-over state buffered the incomplete sequence; Flush() at
            // terminal completion must still surface SOMETHING for it -- a genuinely truncated
            // stream can't recover the missing byte, so this proves Flush ran (U+FFFD), not that
            // the byte was magically recovered.
            await Assert.That(string.Concat(surface.Fed)).Contains("�");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Has_terminal_false_over_rpc_renders_a_bare_note_with_no_family_token() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, factory, new FakeTimeProvider(), agentId: "a1");

            // "pi" is mapped to rpc; hasTerminal:false leaves EffectiveFamily at "rpc" unchanged
            // (no pty guess to correct), so this must render bare -- never "runs over RPC", an
            // internal transport token, not a user-facing concept.
            daemon.Agents.AddOrUpdate(Agent("a1", "pi", hasTerminal: false));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.NoTerminal);
            await Assert.That(vm.State.Detail).IsEqualTo("This session has no terminal.");
            await Assert.That(factory.Created.Count).IsEqualTo(0);
        });
    }
}
