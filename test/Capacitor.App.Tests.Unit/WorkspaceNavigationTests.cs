using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Navigation between the tabbed shell and a session workspace: the surface swap, the two entry
/// points (card click and launch auto-open), every exit path (Back, open-another, intercepted
/// close-to-hide, shutdown), and the generation/latch guards that keep a late launch success from
/// attaching an invisible terminal (spec §3, "Workspace ownership" / "Entry-point guards").
///
/// Opening a workspace builds a REAL WorkspaceViewModel (over the fake daemon and the scripted
/// attach factory), whose TerminalTabViewModel reaches Dispatcher.UIThread.InvokeAsync on every
/// dto push and again in its teardown — so every test runs through RunOnUiAsync (DispatchAsync for
/// a live pumped dispatcher, WithImmediateRxScheduler so ObserveOn(RxSchedulers.MainThreadScheduler)
/// applies synchronously) and carries [NotInParallel("AvaloniaSession")], exactly like
/// WorkspaceViewModelTests/TerminalTabViewModelTests.
public class WorkspaceNavigationTests {
    const string Id1 = "0123456789abcdef0123456789abcdef";
    const string Id2 = "fedcba9876543210fedcba9876543210";
    const string UnusableId = "Launched, but the session id was unusable — open it from the session list.";

    sealed class FakeTerminalSurface : ITerminalSurface {
        public void Feed(string text) { }
        public event Action<byte[]>? InputProduced;
        public event Action<int, int>? Resized;
        public void RaiseInput(byte[] bytes) => InputProduced?.Invoke(bytes);
        public void RaiseResize(int cols, int rows) => Resized?.Invoke(cols, rows);
        public (int Cols, int Rows) CurrentSize { get; set; } = (80, 24);
    }

    /// The tracker as the VM sees it (Action&lt;Func&lt;Task&gt;&gt;): records every registration and
    /// starts it immediately, exactly like WorkspaceTeardownTracker.Track — so a test can count
    /// registrations AND await the teardown that registration started.
    sealed class RecordingTeardownTracker {
        readonly List<Task> _started = [];
        public List<Func<Task>> Registered { get; } = [];

        public void Track(Func<Task> teardown) {
            Registered.Add(teardown);
            _started.Add(teardown());
        }

        public Task StartedTeardowns() => Task.WhenAll(_started.ToArray());
    }

    sealed class FixedLaunchClient : ILaunchClient {
        public LaunchOutcome Next = new(true, Id1, null);
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) => Task.FromResult(Next);
    }

    /// Holds a launch open so a test can navigate (close-to-hide, open another) WHILE the launch is
    /// in flight — the exact window a stale generation has to close.
    sealed class GatedLaunchClient : ILaunchClient {
        public readonly TaskCompletionSource<LaunchOutcome> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) => Gate.Task;
    }

    static AgentStatusDto Agent(string id, bool? hasTerminal = true, string vendor = "claude") => new(
        id, "agent", vendor, "/repo/myproj", "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, HasTerminal: hasTerminal);

    static AgentActionService NewActions() =>
        new(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(),
            new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None, NeverConfirm.Confirm);

    /// The composition root's shape with its three navigation seams faked: a SHARED navigation gate
    /// (a window the coordinator rebuilds gets the same one), the tracker as a delegate, and a
    /// workspace factory that records what it was asked to open.
    sealed class Nav {
        public required FakeDaemonClientService Daemon { get; init; }
        public required FakeTerminalAttachClientFactory Attach { get; init; }
        public required RecordingTeardownTracker Tracker { get; init; }
        public required List<string> Opened { get; init; }
        public required NavigationGate Gate { get; init; }
        public required MainWindowViewModel Vm { get; init; }
    }

    static Nav NewNav(NavigationGate? gate = null, Action<Func<Task>>? track = null) {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var tracker = new RecordingTeardownTracker();
        var opened = new List<string>();
        var actions = NewActions();
        gate ??= new NavigationGate();

        var vm = new MainWindowViewModel(
            daemon, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(),
            navigation: gate,
            trackWorkspaceTeardown: track ?? tracker.Track,
            workspaceFactory: agentId => {
                opened.Add(agentId);
                return new WorkspaceViewModel(
                    agentId, daemon, actions, attach.Factory, () => new FakeTerminalSurface(), time);
            });

        return new Nav {
            Daemon = daemon, Attach = attach, Tracker = tracker, Opened = opened, Gate = gate, Vm = vm,
        };
    }

    /// Opens a workspace whose terminal has actually attached — the only state in which a teardown
    /// can be proven by DetachAsync/DisposeAsync on the scripted client.
    static async Task<FakeTerminalAttachClient> OpenAttachedAsync(Nav nav, string agentId) {
        nav.Daemon.Agents.AddOrUpdate(Agent(agentId));
        nav.Vm.OpenSession(agentId);
        await (nav.Vm.CurrentWorkspace!.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        return nav.Attach.Created[^1];
    }

    static HomeViewModel NewHome(Nav nav, ILaunchClient launch, string statePath) =>
        new(nav.Daemon, new AppStateStore(statePath), launch, () => Task.FromResult(Array.Empty<string>()),
            openSession: nav.Vm.OpenSession,
            navigationGeneration: () => nav.Vm.NavigationGeneration,
            openSessionIfCurrent: nav.Vm.OpenSessionIfCurrent);

    // See the class doc comment: WithImmediateRxScheduler alone never pumps Dispatcher.UIThread,
    // which every workspace open and every teardown reaches.
    static Task RunOnUiAsync(Func<Task> body) =>
        AvaloniaSession.DispatchAsync(async () => {
            await AvaloniaSession.WithImmediateRxScheduler(body);
            return true;
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_session_swaps_to_a_workspace_and_close_returns_to_shell() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();

            nav.Vm.OpenSession(Id1);

            await Assert.That(nav.Vm.CurrentWorkspace).IsNotNull();
            await Assert.That(nav.Vm.CurrentWorkspace!.AgentId).IsEqualTo(Id1);
            // The window binds Back to the VM's own close command — never left null on a workspace
            // the VM itself built (WorkspaceView hides the button while it is).
            await Assert.That(nav.Vm.CurrentWorkspace!.BackCommand).IsNotNull();

            await nav.Vm.CurrentWorkspace!.BackCommand!.Execute();

            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Tracker.Registered.Count).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_another_session_tears_down_the_previous_workspace_tracked() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();
            var first = await OpenAttachedAsync(nav, Id1);

            nav.Daemon.Agents.AddOrUpdate(Agent(Id2));
            nav.Vm.OpenSession(Id2);
            await nav.Tracker.StartedTeardowns();

            await Assert.That(nav.Vm.CurrentWorkspace!.AgentId).IsEqualTo(Id2);
            await Assert.That(nav.Opened).IsEquivalentTo([Id1, Id2]);
            // Exactly one registration: the OUTGOING workspace's, never the incoming one's.
            await Assert.That(nav.Tracker.Registered.Count).IsEqualTo(1);
            await Assert.That(first.DetachCalls).IsEqualTo(1);
            await Assert.That(first.DisposeCalls).IsGreaterThanOrEqualTo(1);
        });
    }

    /// Drives MainWindowCoordinator.OnWindowClosing itself — the ACTUAL intercepted-close path,
    /// wired the way the composition root wires it — rather than calling CloseWorkspace directly:
    /// the window stays alive on a hide, so nothing but this wiring stops an invisible terminal
    /// from staying attached and clamping the PTY for every other viewer (spec §3).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Intercepted_close_to_hide_tears_down_and_resets_navigation() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();
            var coordinator = new MainWindowCoordinator(
                () => {
                    var window = new MainWindow { DataContext = nav.Vm };
                    window.Show(); // the production factory (App.BuildAndShowMainWindow) shows too
                    return window;
                },
                releaseWorkspace: window => (window.DataContext as MainWindowViewModel)?.CloseWorkspace());

            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();

            var client = await OpenAttachedAsync(nav, Id1);
            Dispatcher.UIThread.RunJobs();
            var generationBefore = nav.Vm.NavigationGeneration;

            var intercepted = coordinator.OnWindowClosing();
            await nav.Tracker.StartedTeardowns();

            await Assert.That(intercepted).IsTrue();                  // hidden, not closed
            await Assert.That(coordinator.Window!.IsVisible).IsFalse();
            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();      // reopening lands on the shell
            await Assert.That(nav.Vm.NavigationGeneration).IsGreaterThan(generationBefore);
            await Assert.That(client.DetachCalls).IsEqualTo(1);
            await Assert.That(client.DisposeCalls).IsGreaterThanOrEqualTo(1);

            coordinator.QuitInProgress = true; // let the window really close, as a quit would
            coordinator.Window!.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// The OTHER close (spec §3, real close): nothing intercepts it, the coordinator DISCARDS the
    /// window, and the next ShowMainWindow builds a fresh one — so the discarded VM's workspace
    /// teardown has to start as part of this close, or the attach outlives the window that owned it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_real_close_releases_the_discarded_windows_workspace() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();
            var coordinator = new MainWindowCoordinator(
                () => {
                    var window = new MainWindow { DataContext = nav.Vm };
                    window.Show();
                    return window;
                },
                releaseWorkspace: window => (window.DataContext as MainWindowViewModel)?.CloseWorkspace());

            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();

            var client = await OpenAttachedAsync(nav, Id1);
            Dispatcher.UIThread.RunJobs();

            // QuitInProgress is what a quit sets, and it is exactly what makes the close REAL: the
            // interceptor stands down, Closing is not cancelled, and Closed fires.
            coordinator.QuitInProgress = true;
            coordinator.Window!.Close();
            Dispatcher.UIThread.RunJobs();
            await nav.Tracker.StartedTeardowns();

            await Assert.That(coordinator.Window).IsNull(); // discarded; the next Show builds afresh
            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Tracker.Registered.Count).IsEqualTo(1);
            await Assert.That(client.DetachCalls).IsEqualTo(1);
            await Assert.That(client.DisposeCalls).IsGreaterThanOrEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_generation_launch_success_opens_nothing() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();
            nav.Vm.OpenSession(Id1);
            var captured = nav.Vm.NavigationGeneration;

            nav.Vm.CloseWorkspace(); // any explicit navigation retires the captured generation

            nav.Vm.OpenSessionIfCurrent(Id2, captured);

            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Opened).IsEquivalentTo([Id1]);

            // The same call at the CURRENT generation is the control: the guard is the generation,
            // not a blanket refusal.
            nav.Vm.OpenSessionIfCurrent(Id2, nav.Vm.NavigationGeneration);
            await Assert.That(nav.Vm.CurrentWorkspace!.AgentId).IsEqualTo(Id2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Launch_success_after_close_to_hide_opens_nothing() {
        await RunOnUiAsync(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var nav = NewNav();
            var launch = new GatedLaunchClient();
            using var home = NewHome(nav, launch, path);

            await home.SelectRepositoryAsync("/repo/myproj");
            var start = home.StartCommand.Execute().ToTask(); // generation captured before the launch

            nav.Vm.CloseWorkspace(); // stands in for the coordinator's close-to-hide: bumps
            launch.Gate.SetResult(new LaunchOutcome(true, Id1, null));
            await start;

            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Opened).IsEmpty();
            await Assert.That(home.StartError).IsNull(); // the launch itself succeeded
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_session_after_shutdown_latch_creates_nothing() {
        await RunOnUiAsync(async () => {
            var nav = NewNav();

            nav.Vm.LatchShutdown();
            nav.Vm.OpenSession(Id1);
            nav.Vm.OpenSessionIfCurrent(Id1, nav.Vm.NavigationGeneration);

            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Opened).IsEmpty();
            await Assert.That(nav.Attach.Created).IsEmpty(); // no client, therefore no socket
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shutdown_first_pass_registers_the_live_workspace_before_drain() {
        await RunOnUiAsync(async () => {
            // The REAL tracker here: the point is that the registration lands before the drain
            // seals it, which only the real seal-and-drain can show.
            var tracker = new WorkspaceTeardownTracker(new FakeTimeProvider());
            var nav = NewNav(track: tracker.Track);
            var client = await OpenAttachedAsync(nav, Id1);

            nav.Vm.LatchShutdown();
            await Assert.That(nav.Vm.CurrentWorkspace).IsNull(); // unhooked synchronously

            await tracker.DrainAsync();

            await Assert.That(client.DetachCalls).IsEqualTo(1);
            await Assert.That(client.DisposeCalls).IsGreaterThanOrEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_window_built_after_shutdown_began_cannot_open_a_workspace() {
        await RunOnUiAsync(async () => {
            var gate = new NavigationGate();
            var first = NewNav(gate);
            first.Vm.LatchShutdown();

            // MainWindowCoordinator can build a window between the two shutdown passes — it shares
            // the composition root's gate, so the latch reaches it by construction.
            var rebuilt = NewNav(gate);
            rebuilt.Vm.OpenSession(Id1);

            await Assert.That(rebuilt.Vm.CurrentWorkspace).IsNull();
            await Assert.That(rebuilt.Opened).IsEmpty();
            await Assert.That(rebuilt.Attach.Created).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("agent-2")]                                  // not hex
    [Arguments("0123456789abcdef0123456789abcde")]          // 31 chars
    public async Task Malformed_launch_agent_id_surfaces_an_error_and_opens_nothing(string? agentId) {
        await RunOnUiAsync(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var nav = NewNav();
            var launch = new FixedLaunchClient { Next = new LaunchOutcome(true, agentId, null) };
            using var home = NewHome(nav, launch, path);

            await home.SelectRepositoryAsync("/repo/myproj");
            home.Goal = "do the thing";
            await home.StartCommand.Execute();

            await Assert.That(home.StartError).IsEqualTo(UnusableId);
            await Assert.That(nav.Vm.CurrentWorkspace).IsNull();
            await Assert.That(nav.Opened).IsEmpty();
            await Assert.That(home.Goal).IsEqualTo(""); // the launch DID start — the goal is spent
        });
    }

    /// The card click's own entry point (HomeView routes a click to this), distinct from the launch
    /// auto-open above: no generation is involved, the click IS the current navigation.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_card_click_opens_that_sessions_workspace() {
        await RunOnUiAsync(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var nav = NewNav();
            using var home = NewHome(nav, new FixedLaunchClient(), path);

            home.OpenSessionRequested(Id2);

            await Assert.That(nav.Vm.CurrentWorkspace!.AgentId).IsEqualTo(Id2);
        });
    }
}
