using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// TimeProvider double whose GetUtcNow() is settable between assertions — distinct from a
/// fixed-at-construction TimeProvider because the uptime tests need to advance "now" between
/// ticker ticks without touching the real clock.
sealed class MutableTimeProvider : TimeProvider {
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

/// Covers UptimeFormat (spec §8 boundary table), AgentRowViewModel's presentation projection and
/// ActionsEnabled/Uptime OAPHs in isolation (plain Subjects, no Avalonia session needed — the row
/// itself is scheduler-agnostic, see its class doc comment), and the full MainWindowViewModel
/// pipeline (sort order, EditDiff removal, GridEnabled, banner) driven through
/// AvaloniaSession.DispatchAsync with the REAL dispatcher scheduler.
///
/// The full-pipeline tests deliberately do NOT use AvaloniaSession.WithImmediateRxScheduler:
/// MainWindowViewModel's shared ticker is a real Observable.Interval(1s, RxSchedulers.
/// MainThreadScheduler), and subscribing an Interval to ImmediateScheduler.Instance blocks/spins
/// the calling thread forever (Rx's ImmediateScheduler executes a relative-time Schedule by
/// SLEEPING for the real due time rather than truly firing "immediately" — verified against the
/// installed System.Reactive 6.1.0 before writing this comment). Any test that populates
/// service.Agents and exercises the resulting rows must run under the real (non-blocking)
/// AvaloniaScheduler instead. The banner's Observable.Timer has the identical hazard, bounded by
/// the BannerLifetime seam (TimeSpan.Zero under an immediate scheduler skips the sleep entirely).
public class AgentGridTests {
    // ---- UptimeFormat (spec §8) ----

    [Test]
    [Arguments(0, "0s")]
    [Arguments(-5, "0s")] // negative clamps
    [Arguments(59, "59s")]
    [Arguments(60, "1m")]
    [Arguments(3599, "59m")] // 59m59s: minutes-only bucket drops seconds
    [Arguments(3600, "1h")] // exact hour: zero-remainder drops the minutes unit
    [Arguments(86340, "23h 59m")]
    [Arguments(86400, "1d")] // exact day: zero-remainder drops the hours unit
    [Arguments(90000, "1d 1h")]
    public async Task Format_boundary_table(int seconds, string expected) {
        await Assert.That(UptimeFormat.Format(TimeSpan.FromSeconds(seconds))).IsEqualTo(expected);
    }

    // ---- AgentRowViewModel projection (spec §8) ----

    static AgentStatusDto Dto(
            string id = "a", string kind = "agent", string vendor = "claude", string? repoPath = "/repos/kcap-cli",
            string status = "Running", string? requester = null, DateTime? createdAt = null, string? model = null) =>
        new(id, kind, vendor, repoPath, status, null, null, requester, createdAt ?? DateTime.UtcNow, model);

    static AgentRowViewModel NewRow(
            AgentStatusDto dto, AgentActionService? actions = null, IObservable<long>? ticker = null,
            TimeProvider? time = null, IObservable<bool>? connected = null,
            IObservable<IReadOnlySet<string>>? stopsInFlight = null) =>
        new(dto,
            actions ?? new AgentActionService(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(), new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None),
            ticker ?? new Subject<long>(),
            time ?? TimeProvider.System,
            connected ?? new BehaviorSubject<bool>(true),
            stopsInFlight ?? new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>()));

    [Test]
    public async Task VendorDisplay_is_vendor_only_when_model_is_null() {
        var row = NewRow(Dto(vendor: "claude", model: null));
        await Assert.That(row.VendorDisplay).IsEqualTo("claude");
    }

    [Test]
    public async Task VendorDisplay_appends_model_in_parens_when_present() {
        var row = NewRow(Dto(vendor: "claude", model: "opus"));
        await Assert.That(row.VendorDisplay).IsEqualTo("claude (opus)");
    }

    [Test]
    public async Task RepoLeaf_and_RepoFull_reflect_the_path() {
        var row = NewRow(Dto(repoPath: "/repos/kcap-cli"));
        await Assert.That(row.RepoLeaf).IsEqualTo("kcap-cli");
        await Assert.That(row.RepoFull).IsEqualTo("/repos/kcap-cli");
    }

    [Test]
    public async Task RepoLeaf_is_em_dash_and_RepoFull_empty_when_path_is_null() {
        var row = NewRow(Dto(repoPath: null));
        await Assert.That(row.RepoLeaf).IsEqualTo("—");
        await Assert.That(row.RepoFull).IsEqualTo("");
    }

    [Test]
    public async Task Requester_null_renders_unknown() {
        var row = NewRow(Dto(requester: null));
        await Assert.That(row.Requester).IsEqualTo("unknown");
    }

    [Test]
    public async Task Requester_present_renders_verbatim() {
        var row = NewRow(Dto(requester: "alice"));
        await Assert.That(row.Requester).IsEqualTo("alice");
    }

    [Test]
    public async Task StatusText_is_verbatim() {
        var row = NewRow(Dto(status: "Completed"));
        await Assert.That(row.StatusText).IsEqualTo("Completed");
    }

    [Test]
    public async Task Id_and_Kind_come_straight_from_the_dto() {
        var row = NewRow(Dto(id: "agent-7", kind: "review-flow"));
        await Assert.That(row.Id).IsEqualTo("agent-7");
        await Assert.That(row.Kind).IsEqualTo("review-flow");
    }

    // ---- ActionsEnabled matrix (spec §8: connected && !inFlight(Id)) ----

    [Test]
    public async Task ActionsEnabled_true_when_connected_and_not_in_flight() {
        var row = NewRow(Dto(id: "a"),
            connected: new BehaviorSubject<bool>(true),
            stopsInFlight: new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>()));
        await Assert.That(row.ActionsEnabled).IsTrue();
    }

    [Test]
    public async Task ActionsEnabled_false_when_disconnected() {
        var row = NewRow(Dto(id: "a"),
            connected: new BehaviorSubject<bool>(false),
            stopsInFlight: new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>()));
        await Assert.That(row.ActionsEnabled).IsFalse();
    }

    [Test]
    public async Task ActionsEnabled_false_while_its_own_id_is_in_flight() {
        var row = NewRow(Dto(id: "a"),
            connected: new BehaviorSubject<bool>(true),
            stopsInFlight: new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string> { "a" }));
        await Assert.That(row.ActionsEnabled).IsFalse();
    }

    [Test]
    public async Task ActionsEnabled_unaffected_by_a_DIFFERENT_id_in_flight() {
        var row = NewRow(Dto(id: "a"),
            connected: new BehaviorSubject<bool>(true),
            stopsInFlight: new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string> { "b" }));
        await Assert.That(row.ActionsEnabled).IsTrue();
    }

    [Test]
    public async Task ActionsEnabled_flips_live_as_the_sources_change() {
        var connected = new BehaviorSubject<bool>(true);
        var stopsInFlight = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
        var row = NewRow(Dto(id: "a"), connected: connected, stopsInFlight: stopsInFlight);

        await Assert.That(row.ActionsEnabled).IsTrue();

        stopsInFlight.OnNext(new HashSet<string> { "a" });
        await Assert.That(row.ActionsEnabled).IsFalse();

        stopsInFlight.OnNext(new HashSet<string>());
        await Assert.That(row.ActionsEnabled).IsTrue();

        connected.OnNext(false);
        await Assert.That(row.ActionsEnabled).IsFalse();
    }

    // ---- Uptime (spec §8) ----

    [Test]
    public async Task Uptime_seeds_from_time_at_construction() {
        var time = new MutableTimeProvider { Now = new DateTimeOffset(2026, 8, 6, 10, 0, 5, TimeSpan.Zero) };
        var createdAt = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
        var row = NewRow(Dto(createdAt: createdAt), time: time, ticker: new Subject<long>());

        await Assert.That(row.Uptime).IsEqualTo("5s");
    }

    [Test]
    public async Task Uptime_recomputes_on_every_ticker_tick() {
        var time = new MutableTimeProvider { Now = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero) };
        var createdAt = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
        var ticker = new Subject<long>();
        var row = NewRow(Dto(createdAt: createdAt), time: time, ticker: ticker);

        await Assert.That(row.Uptime).IsEqualTo("0s");

        time.Now = time.Now.AddSeconds(65);
        ticker.OnNext(1);
        await Assert.That(row.Uptime).IsEqualTo("1m");

        time.Now = time.Now.AddHours(2);
        ticker.OnNext(2);
        await Assert.That(row.Uptime).IsEqualTo("2h 1m");
    }

    [Test]
    public async Task CreatedAt_is_treated_as_UTC_regardless_of_the_dto_DateTimeKind() {
        // The wire value has no Kind (STJ deserializes DateTime as Unspecified) — the row must
        // still treat it as UTC (spec §8), not local time.
        var time = new MutableTimeProvider { Now = new DateTimeOffset(2026, 8, 6, 10, 0, 10, TimeSpan.Zero) };
        var unspecified = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Unspecified);
        var row = NewRow(Dto(createdAt: unspecified), time: time, ticker: new Subject<long>());

        await Assert.That(row.Uptime).IsEqualTo("10s");
    }

    // ---- Stop/OpenInWeb delegation (spec §7 — same code path as the tray) ----

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task StopCommand_requests_stop_with_kind_vendor_repo_label() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var actions = new AgentActionService(ops, notifier, new RecordingOpener(), new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None);
        var row = NewRow(Dto(id: "a", kind: "agent", vendor: "claude", repoPath: "/repos/kcap-cli"), actions: actions);

        ops.QueueStop(new StopAgentResult(false, "failed", null));
        row.StopCommand.Execute().Subscribe();

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "stop banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent · claude · kcap-cli"], CollectionOrdering.Matching);
        await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", false)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OpenInWebCommand_opens_the_agent_url() {
        var opener = new RecordingOpener();
        var snapshots = new ReplaySubject<DaemonStatusDto>(1);
        snapshots.OnNext(Snap(serverUrl: "https://x.kcap.ai"));
        var actions = new AgentActionService(new ScriptedLocalControlOps(), new RecordingNotifier(), opener, snapshots, CancellationToken.None);
        var row = NewRow(Dto(id: "a"), actions: actions);

        row.OpenInWebCommand.Execute().Subscribe();

        await Assert.That(opener.Opened).IsEquivalentTo(["https://x.kcap.ai/agents/a"], CollectionOrdering.Matching);
    }

    // ---- Full MainWindowViewModel pipeline (real AvaloniaScheduler — see class doc comment) ----

    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service, ScriptedLocalControlOps? ops = null) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(ops ?? new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None);
        return (actions, notifier);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Agents_sort_by_created_at_then_id_ordinal() {
        var ids = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            var t0 = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
            service.Agents.AddOrUpdate(Dto(id: "z", createdAt: t0));
            service.Agents.AddOrUpdate(Dto(id: "a", createdAt: t0)); // tie on CreatedAt: ordinal tiebreak
            service.Agents.AddOrUpdate(Dto(id: "b", createdAt: t0.AddMinutes(-5))); // earliest
            Dispatcher.UIThread.RunJobs();

            return vm.Agents.Select(r => r.Id).ToList();
        });

        await Assert.That(ids).IsEquivalentTo(["b", "a", "z"], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Row_disappears_when_the_agent_leaves_the_snapshot_via_EditDiff() {
        var (beforeCount, afterIds) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            var t0 = DateTime.UtcNow;
            var initial = new List<AgentStatusDto> { Dto(id: "a", createdAt: t0), Dto(id: "b", createdAt: t0) };
            service.Agents.EditDiff(initial, EqualityComparer<AgentStatusDto>.Default);
            Dispatcher.UIThread.RunJobs();
            var before = vm.Agents.Count;

            // "a" stopped and left the next snapshot entirely (spec §7 — no local removal, only
            // the snapshot's absence drives this).
            service.Agents.EditDiff([Dto(id: "b", createdAt: t0)], EqualityComparer<AgentStatusDto>.Default);
            Dispatcher.UIThread.RunJobs();

            return (before, vm.Agents.Select(r => r.Id).ToList());
        });

        await Assert.That(beforeCount).IsEqualTo(2);
        await Assert.That(afterIds).IsEquivalentTo(["b"], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task GridEnabled_reflects_attach_state() {
        var (whileConnected, whileUnreachable) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            Dispatcher.UIThread.RunJobs();
            var connected = vm.GridEnabled;

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            Dispatcher.UIThread.RunJobs();
            var unreachable = vm.GridEnabled;

            return (connected, unreachable);
        });

        await Assert.That(whileConnected).IsTrue();
        await Assert.That(whileUnreachable).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rows_persist_and_ActionsEnabled_turns_false_when_disconnected() {
        var (idsWhileConnected, idsWhileDisconnected, actionsEnabledWhileDisconnected) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            service.Agents.AddOrUpdate(Dto(id: "a"));
            Dispatcher.UIThread.RunJobs();
            var connectedIds = vm.Agents.Select(r => r.Id).ToList();

            // Retention (spec §8): the cache is not cleared on disconnect, so the row must still
            // be there — only its actions disable.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            Dispatcher.UIThread.RunJobs();
            var disconnectedIds = vm.Agents.Select(r => r.Id).ToList();
            var actionsEnabled = vm.Agents[0].ActionsEnabled;

            return (connectedIds, disconnectedIds, actionsEnabled);
        });

        await Assert.That(idsWhileConnected).IsEquivalentTo(["a"], CollectionOrdering.Matching);
        await Assert.That(idsWhileDisconnected).IsEquivalentTo(["a"], CollectionOrdering.Matching);
        await Assert.That(actionsEnabledWhileDisconnected).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ActionsEnabled_turns_false_while_its_stop_is_in_flight() {
        var ops = new ScriptedLocalControlOps();
        var gate = ops.ArmStop();

        var (beforeStop, whileInFlight) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service, ops);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            service.Agents.AddOrUpdate(Dto(id: "a"));
            Dispatcher.UIThread.RunJobs();
            var before = vm.Agents[0].ActionsEnabled;

            actions.RequestStop("a", "agent-a"); // pushes StopsInFlight synchronously (AgentActionService contract)
            Dispatcher.UIThread.RunJobs(); // crosses the row's ObserveOn'd stopsInFlight
            var inFlight = vm.Agents[0].ActionsEnabled;

            return (before, inFlight);
        });

        gate.SetResult(new StopAgentResult(true, "stopped", null)); // release the held stop so the test process doesn't leave a dangling task

        await Assert.That(beforeStop).IsTrue();
        await Assert.That(whileInFlight).IsFalse();
    }

    // ---- Banner (spec §11) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Banner_latest_message_wins_and_replaces_the_pending_one() {
        var (first, second) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            // Default BannerLifetime (6s): safe here because the REAL AvaloniaScheduler's
            // Schedule with a due time posts a dispatcher timer instead of blocking — Notify()
            // returns immediately either way, and this test never waits for that timer to fire.
            notifier.Notify("first");
            Dispatcher.UIThread.RunJobs();
            var f = vm.Banner;

            notifier.Notify("second");
            Dispatcher.UIThread.RunJobs();
            var s = vm.Banner;

            return (f, s);
        });

        await Assert.That(first).IsEqualTo("first");
        await Assert.That(second).IsEqualTo("second");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Banner_auto_clears_after_the_lifetime_elapses() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            // TimeSpan.Zero (spec §11 seam): under the immediate scheduler this skips the
            // relative-time sleep entirely rather than blocking the test for 6 real seconds.
            vm.BannerLifetime = TimeSpan.Zero;

            // Plain INotifyPropertyChanged (not WhenAnyValue) — this headless test harness never
            // calls RxAppBuilder.BuildApp(), which WhenAnyValue's ObservableForProperty requires
            // but the app's own UseReactiveUI()/AvaloniaScheduler wiring does not.
            var seen = new List<string?>();
            vm.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(MainWindowViewModel.Banner)) seen.Add(vm.Banner);
            };

            notifier.Notify("boom");

            await Assert.That(seen.Contains("boom")).IsTrue();
            await Assert.That(vm.Banner).IsNull();
        });
    }
}
