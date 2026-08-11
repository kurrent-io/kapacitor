using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App;

public partial class App : Application {
    // AI-1654 §3.6: app shutdown WAITS (does not cancel) for an in-flight lifecycle mutation, but
    // only up to this cap — an internally-triggered mutation (startup matrix, skew, txn-requery)
    // has no other shutdown-token wiring, so an uncapped wait could hang shutdown forever.
    static readonly TimeSpan QuiesceShutdownCap = TimeSpan.FromSeconds(60);

    // Linked to the app's shutdown sequence below; the token StartDaemonCommand's WAIT is
    // built against (Task 4 carry-note: never CancellationToken.None — an unbounded wait would
    // survive app exit).
    readonly CancellationTokenSource _shutdown = new();
    DaemonClientService? _service; // concrete type: IAsyncDisposable is not on the interface
    // AI-1654: subscribed and Start()'d BEFORE _service.Start() begins pumping (subscribe-before-
    // pump — DaemonLifecycleController.Start's own doc comment). Disposed before _service in every
    // teardown path below: it's the dependent (subscribes to _service's streams), so it goes first.
    DaemonLifecycleController? _lifecycle;
    // AI-1654 Task 24: no disposal needed — it holds no subscription of its own, only a one-shot
    // await chain against BuildLifecycleController's cliPath/probe/store/surface and _shutdown.Token,
    // so cancelling _shutdown (every teardown path below already does) is what stops it.
    ShimOfferCoordinator? _shimOffer;
    // Assigned by StartAsync's success path only; every one is still null on a startup failure
    // (and cleared again by the catch, which disposes whatever had been built). Teardown —
    // shutdown and startup-failure alike — disposes them in reverse creation order, tray icon
    // first, so a quit never strands a dead icon in the menu bar (spec §9).
    MainWindowCoordinator? _coordinator;
    PauseController? _pause;
    ConsentService? _consent;
    ConsentPromptCoordinator? _promptCoordinator;
    // Disposed with the other UI services below: it holds a constructor-scoped subscription to
    // the shared ticker, which is RefCount'd — an undisposed subscriber keeps the Interval (and
    // this object) running past teardown. Held as a field so it survives StartAsync's own stack
    // frame: the prompt window factory and BuildAndShowMainWindow both close over the SAME
    // instance.
    ActivityViewModel? _activity;
    TrayViewModel? _trayVm;
    TrayIconManager? _tray;
    // No disposal needed — RefCount tears its Interval down with its last subscriber, and every
    // subscriber above IS disposed. Held so the consent prompt and the activity feed share the
    // same 1 Hz heartbeat.
    UiTicker? _ticker;
    bool _shutdownStarted;
    bool _shutdownConfirmed;
    // 0 = normal shutdown. Set to 1 on a startup failure so the DEFERRED shutdown path (Cmd+Q /
    // platform shutdown while the error window is showing — OnShutdownRequested ->
    // DisposeAndShutdownAsync) still reports failure, instead of TryShutdown()'s platform
    // default of 0 silently overwriting it.
    int _exitCode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // The steady-state mode (spec §9): closing the main window hides it to the tray, so
            // the app must never exit on last-window-close. Set here, before StartAsync fires, so
            // it holds from the very first window onward; ShowStartupError pins the same value
            // again on the failure path, where it is now redundant but self-documenting (its own
            // comment explains the exit-code bug that pin fixes).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // This continuation is the ONLY path to a visible window: OnFrameworkInitializationCompleted
    // fires it fire-and-forget and returns immediately, so an exception escaping here would
    // otherwise leave a live process with an empty dispatcher loop, no window, and no error
    // surface (stderr is invisible for a GUI-launched WinExe) — it must fail loudly instead.
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        try {
            var service = await DaemonClientService.CreateDefaultAsync();

            // One LocalControlOps and one AppNotifier for the whole app: the tray menu and the
            // window rows share a single stop/open-in-web code path (spec §7) and a single
            // toast/stderr channel (spec §11). notifier is built here (not after service.Start()
            // below) because PauseController/AgentActionService, constructed further down, need it.
            var ops = new LocalControlOps(service.DaemonName);
            var notifier = new AppNotifier();

            // AI-1654 Task 22: BehaviorSubjects, not plain Subjects — MainWindowViewModel and
            // TrayViewModel don't exist yet at this point in StartAsync (built further down), so a
            // BehaviorSubject replays its latest value to whichever one subscribes later, meaning a
            // Status/Attention call this early (the startup-phase reconciliation, e.g.) is never
            // silently dropped for having no subscriber yet.
            var lifecycleStatus    = new BehaviorSubject<string?>(null);
            var lifecycleAttention = new BehaviorSubject<string?>(null);

            // AI-1654 subscribe-before-pump: the controller's attach subscription must be live
            // BEFORE service.Start() begins pumping, or the startup phase could miss the very
            // first terminal outcome it hinges on (DaemonLifecycleController.Start's own comment).
            var (lifecycle, shimOffer) = BuildLifecycleController(service, lifecycleStatus.OnNext, lifecycleAttention.OnNext);
            lifecycle.Start();
            _lifecycle = lifecycle;
            // Task 24: unlike lifecycle's Start(), subscribe-before-run doesn't matter here —
            // Offerable is a BehaviorSubject, so TrayViewModel (built further below) still sees
            // the current value the moment it subscribes.
            shimOffer.Start();
            _shimOffer = shimOffer;

            service.Start();
            _service = service;

            var ticker = new UiTicker();
            _ticker = ticker;
            _pause = new PauseController(ops, notifier.Notify, _shutdown.Token);
            // ConfirmForceStopAsync reads _coordinator at INVOCATION time (a captured field, not
            // a captured value) — safe even though _coordinator is still null right here, because
            // nothing can trigger a protected-kind stop before ShowMainWindow below assigns it.
            var actions = new AgentActionService(ops, notifier, new ShellUrlOpener(), service.Snapshots, _shutdown.Token, ConfirmForceStopAsync);

            // Constructed once here, like the ticker and consent service (spec §7): the prompt
            // window factory below and MainWindowViewModel both need the SAME instance — the
            // former to nudge it on every conclusive ack, the latter to render it.
            var activity = new ActivityViewModel(
                () => ConsentDecisionLogReader.ReadTail(service.DaemonName, 200),
                () => ActivityStatKey(service.DaemonName), ticker);
            _activity = activity;

            // The prompt window is built per raise, never here: the coordinator owns its lifetime
            // and each window gets its own ViewModel over the one shared service (spec §6).
            var consent = new ConsentService(
                service, ops, ticker, ct => ConsentSubscription.RunAsync(service.DaemonName, ct),
                TimeProvider.System, _shutdown.Token);
            _consent = consent;
            _promptCoordinator = new ConsentPromptCoordinator(consent, () => new ConsentPromptWindow {
                DataContext = new ConsentPromptViewModel(
                    consent, notifier, ticker, TimeProvider.System, _shutdown.Token, activity.RequestRefresh),
                Notifier = notifier,
            });

            _coordinator = new MainWindowCoordinator(
                () => BuildAndShowMainWindow(service, actions, notifier, ticker, _shutdown.Token, activity, lifecycle.StartActionAsync, lifecycleStatus));
            // A shutdown that started before this continuation resumed already ran its first
            // pass against a null coordinator, so a window built now must never be
            // close-protected (BeginShutdownPass's rule 1 is the general defense; this is the
            // by-construction one, and it is why the window below cannot even briefly intercept).
            _coordinator.QuitInProgress = _shutdownStarted;
            _coordinator.ShowMainWindow();
            desktop.MainWindow = _coordinator.Window;

            // LAST, deliberately (spec §9): anything above throwing lands in the catch with no
            // tray icon ever created, leaving the error window as the only surface.
            _trayVm = new TrayViewModel(
                service, _pause, actions, consent, openMainWindow: _coordinator.ShowMainWindow,
                quit: () => desktop.TryShutdown(), openReviewPrompts: _promptCoordinator.ShowPromptWindow,
                lifecycleAttention: lifecycleAttention, shimOfferable: shimOffer.Offerable,
                installShim: shimOffer.RunManualInstallAsync);
            _tray = new TrayIconManager(this, _trayVm);
        } catch (Exception ex) {
            // BEFORE any await: a shutdown request can arrive while cleanup below is still
            // awaiting (or if the helper itself throws), and the deferred path reads this.
            _exitCode = 1;
            // Also before any await, and for the same reason: if the main window was already up
            // when the failure hit, no tray will ever exist to bring it back, so hide-on-close
            // must not intercept anything from here on — every close on this path is a real one.
            if (_coordinator is not null) _coordinator.QuitInProgress = true;
            Console.Error.WriteLine($"kcap app failed to start: {ex}");
            await HandleStartupFailureAsync(
                desktop, ex, _service, _shutdown, [_tray, _trayVm, _promptCoordinator, _consent, _activity, _pause], _lifecycle);
            // all already disposed above — never let a later OnShutdownRequested (e.g. Cmd+Q
            // while the error window is up) dispose any of them a second time
            _service = null;
            _lifecycle = null;
            _shimOffer = null; // no disposal of its own (see field comment) — just drop the reference
            _tray = null;
            _trayVm = null;
            _promptCoordinator = null;
            _consent = null;
            _pause = null;
            _activity = null;
        }
    }

    // Split out of the catch so a test can drive "dispose-then-show-error" against a real
    // DaemonClientService (constructed with fakes, disposal observable) and the same fake
    // IClassicDesktopStyleApplicationLifetime AppStartupTests already uses for ShowStartupError.
    // Ordering matters: dispose WHILE WE STILL CAN. `service` may already be live (Start()
    // called, socket/IPC pump running) if the failure happened later in StartAsync (e.g.
    // BuildAndShowMainWindow throwing) — and the error window's own close handler force-shuts-
    // down via desktop.Shutdown(1), which bypasses OnShutdownRequested/DisposeAndShutdownAsync
    // entirely, so nothing else would ever run this cleanup.
    internal static async Task HandleStartupFailureAsync(
            IClassicDesktopStyleApplicationLifetime desktop, Exception ex, DaemonClientService? service,
            CancellationTokenSource shutdown, IReadOnlyList<IDisposable?> uiDisposables,
            DaemonLifecycleController? lifecycle = null) {
        // The dependent goes first (it subscribes to service's streams) — same ordering rule as
        // the normal shutdown path below. Its own DisposeAsync cancels its independent lifetime
        // token and waits out any mutation it started; that wait is unbounded here on purpose — a
        // startup failure has no live UI to defer against, so there is nothing to keep responsive.
        if (lifecycle is not null) {
            try {
                await lifecycle.DisposeAsync();
            } catch (Exception disposeEx) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during startup-failure cleanup: {disposeEx}");
            }
        }
        if (service is not null) {
            shutdown.Cancel();
            try {
                await service.DisposeAsync();
            } catch (Exception disposeEx) {
                // The ORIGINAL startup exception (ex, already captured and about to be shown
                // below) must never be masked by a secondary dispose failure — append it to the
                // same Console.Error channel instead of letting it propagate.
                Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during startup-failure cleanup: {disposeEx}");
            }
        }
        // Same rule, same reason, for whatever the success path had already built when it threw
        // (tray icon first): the error window's close handler force-shuts-down, so this is their
        // only cleanup too. Entries are null when that step was never reached.
        DisposeAll(uiDisposables, "startup-failure cleanup");
        ShowStartupError(desktop, ex);
    }

    // Split out of the catch so a test can drive it against a fake
    // IClassicDesktopStyleApplicationLifetime (no real windowing/desktop lifetime needed) and
    // assert the ShutdownMode pin, the MainWindow assignment, and the deferred Shutdown(1) all
    // happen in the right order.
    internal static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex) {
        // Redundant since OnFrameworkInitializationCompleted pins the same mode for the whole
        // app (spec §9) — kept because it is what makes THIS path's exit code correct on its own
        // terms, and because the reasoning below is the record of the P1 bug it fixed. It was
        // decompiler-verified against the mode this path used to run under, OnLastWindowClose
        // (the framework default, which the app then set nowhere): Window.HandleClosed raises
        // the CLR Closed event (our handler below, which calls Shutdown(1)) BEFORE the routed
        // WindowClosedEvent that OnLastWindowClose listens for. So closing the error window used
        // to run: our Shutdown(1) (sets _exitCode=1) -> THEN the routed event -> _windows hits 0
        // -> an OnLastWindowClose-driven TryShutdown() with its default exit code 0 ->
        // App.OnShutdownRequested's deferred dance -> a second TryShutdown() whose DoShutdown
        // unconditionally overwrites _exitCode with 0. Net effect: the most common startup
        // failure exited 0. Pinning OnExplicitShutdown disarms that whole OnLastWindowClose
        // branch, so our explicit Shutdown(1) below is the only shutdown and nothing overwrites
        // its exit code.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Showing a window here is legal before Avalonia's main loop starts — it's exactly what
        // StartWithClassicDesktopLifetime's own ShowMainWindow() does right after Start. Calling
        // desktop.Shutdown(1) directly, as this catch used to, is what previously threw when
        // startup faulted synchronously (before the main loop began) — so this shape resolves
        // that pre-main-loop edge case rather than worsening it.
        var errorWindow = BuildStartupErrorWindow(ex);
        if (desktop.MainWindow is null) desktop.MainWindow = errorWindow;
        errorWindow.Closed += (_, _) => desktop.Shutdown(1);
        errorWindow.Show();
    }

    // Last-resort UI for a startup failure: Console.Error above is invisible on a normal GUI
    // launch (OutputType=WinExe has no console), so this window is the only channel that
    // actually reaches the user. SelectableTextBlock (not TextBlock) keeps the stack trace
    // copyable for a bug report.
    internal static Window BuildStartupErrorWindow(Exception ex) =>
        new() {
            Title = "Kurrent Capacitor — startup failed",
            Icon = ProductIcon.WindowIcon,
            Width = 640,
            Height = 400,
            Content = new ScrollViewer {
                Content = new SelectableTextBlock {
                    Text = $"The app failed to start. Details:\n{ex}",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

    // The MainWindowCoordinator's window factory, split out of StartAsync so a test can drive
    // "build VM+window, assign, and Show()" against a fake service without needing a real
    // daemon/profile (CreateDefaultAsync does real config I/O). This is also the actual bug fix:
    // Avalonia's StartWithClassicDesktopLifetime calls
    // ShowMainWindow() exactly ONCE, synchronously, right after Start — and at that moment
    // desktop.MainWindow is still null, because CreateDefaultAsync genuinely awaits (config.json
    // read). By the time this continuation resumes and assigns desktop.MainWindow, nothing else
    // will ever call .Show() for us, so this method must call it explicitly. Show() on an
    // already-visible window is a no-op, so this stays correct even if a future edit changes the
    // timing such that ShowMainWindow() DOES still see a non-null MainWindow.
    internal static MainWindow BuildAndShowMainWindow(
            IDaemonClientService service, AgentActionService actions, IAppNotifier notifier, ITicker ticker,
            CancellationToken shutdownToken, ActivityViewModel activity, Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null) {
        // Notifier is set on the WINDOW (spec §11 toast overlay), not the ViewModel — the toast
        // is a View-level concern (WindowNotificationManager lives on MainWindow) independent of
        // the VM's WhenActivated-scoped projections.
        var window = new MainWindow {
            DataContext = new MainWindowViewModel(service, actions, ticker, shutdownToken, activity, startAction, lifecycleStatus),
            Notifier = notifier,
        };
        window.Show();
        return window;
    }

    // AI-1654 composition: wires the daemon-service CLI facade (decision 1: everything through the
    // CLI), the login-shell PATH probe, and the persisted decline-memory store. A broken
    // KCAP_APP_CLI_PATH override (CliResolver.ResolvePath returning null) is treated as "no CLI"
    // here — unlike CreateDefaultAsync's own lenient `daemon start -d` fallback above, the
    // lifecycle features must never silently point at the wrong binary.
    //
    // Task 24: also builds ShimOfferCoordinator here (not a separate method) — it shares
    // cliPath/probe/store/surface with the lifecycle controller rather than re-resolving them.
    (DaemonLifecycleController Lifecycle, ShimOfferCoordinator ShimOffer) BuildLifecycleController(
            DaemonClientService service, Action<string> setLifecycleStatus, Action<string> setLifecycleAttention) {
        var cliPath = CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists);
        var runner  = new DaemonClientService.ProcessRunner();
        var profile = AppConfig.ResolvedProfile; // already resolved by CreateDefaultAsync above
        var cli     = new KcapCli(runner, cliPath, service.DaemonName, profile?.ProfileName ?? "default", terminalPath: null);
        var probe   = new LoginShellProbe(runner, Environment.GetEnvironmentVariable);
        var store   = new AppStateStore(PathHelpers.ConfigPath("app-state.json"));
        var surface = new LifecycleSurface(setLifecycleStatus, setLifecycleAttention, ConfirmLifecyclePromptAsync);

        var lifecycle = new DaemonLifecycleController(
            service, cli, probe, store, surface, () => Task.FromResult(ValidProfileName(profile)), TimeProvider.System);

        // The shim links to the RESOLVED ABSOLUTE path only — CliResolver's bare "kcap" fallback
        // (no override set, or AI-1653's not-yet-landed bundle-relative arm) means there is
        // nothing to link, so the offer and the menu item both stay off for the whole run.
        var shimTarget = cliPath is not null && Path.IsPathRooted(cliPath) ? cliPath : null;
        var shimOffer = new ShimOfferCoordinator(
            lifecycle.PhaseClosed, probe, new PathShimInstaller(runner, probe), store, surface, shimTarget, _shutdown.Token);

        return (lifecycle, shimOffer);
    }

    static string? ValidProfileName(ResolvedProfile? profile) =>
        !string.IsNullOrWhiteSpace(profile?.ServerUrl) && Uri.TryCreate(profile.ServerUrl, UriKind.Absolute, out _)
            ? profile.ProfileName
            : null;

    Task<bool> ConfirmLifecyclePromptAsync(LifecyclePrompt prompt, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowLifecyclePromptDialogAsync(prompt, ct));

    Task<bool> ShowLifecyclePromptDialogAsync(LifecyclePrompt prompt, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new LifecyclePromptWindow { DataContext = new LifecyclePromptViewModel(prompt, tcs) };
        // Closing via the titlebar/Esc without Accept/Decline also resolves false —
        // BuildConfirmForceStopWindow's same rule below. TrySetResult is idempotent, so this is a
        // no-op once AcceptCommand/DeclineCommand already resolved it.
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        WireDialogCancellation(dialog, tcs, ct);

        if (_coordinator?.Window is { IsVisible: true } owner) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // ConfirmAndTakeoverAsync holds the operation gate across the whole ConfirmAsync await — a
    // dialog left open through a lifetime-cancel (app shutdown or DisposeAsync) must not leave the
    // gate (and therefore QuiescedAsync) blocked on a human who may never come back. Cancellation
    // can arrive on any thread, so the close is posted rather than called inline; the registration
    // is disposed once the dialog resolves on its own so it doesn't outlive the window. Extracted
    // (internal, static) so a test can drive it directly against a real headless window.
    internal static void WireDialogCancellation(Window dialog, TaskCompletionSource<bool> tcs, CancellationToken ct) {
        var registration = ct.Register(() =>
            Dispatcher.UIThread.Post(() => { if (!tcs.Task.IsCompleted) dialog.Close(); }));
        tcs.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static string ActivityStatKey(string daemonName) {
        var path = ConsentDecisionLogReader.PathFor(daemonName);
        return ActivityStatKey(path + ".1", path);
    }

    // Combines both log files' (LastWriteTimeUtc, Length) into one comparison key for
    // ActivityViewModel's stat poll (spec §7). Each file gets its OWN try/catch: `.1` is absent
    // on every fresh install until the first 1MB rotation, and a single shared catch around both
    // files would collapse the WHOLE joined key to the "absent" constant whenever `.1` throws —
    // appends to the live file would then never change the key, and the Activity tab would go
    // stale until the tab is reselected. FileInfo.Length throws FileNotFoundException on a
    // missing file (unlike File.GetLastWriteTimeUtc, which returns a sentinel instead) — that
    // throw is what carries a clean per-file absence into that file's own "absent" branch. Takes
    // both paths directly (rather than a daemon name) so a test can point it at a temp directory
    // without redirecting any real daemon-dir resolution.
    internal static string ActivityStatKey(string p1Path, string livePath) => $"{StatOf(p1Path)}|{StatOf(livePath)}";

    static string StatOf(string path) {
        try {
            return $"{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}";
        } catch {
            return "absent";
        }
    }

    // Composed here (not inside AgentActionService, spec decision 5): the service only awaits the
    // seam; every UI concern — the dialog itself, choosing an owner, marshaling onto the UI
    // thread — lives at this composition root, same as ShellUrlOpener/LocalControlOps above.
    Task<bool> ConfirmForceStopAsync(string label) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowConfirmForceStopDialogAsync(label));

    // Runs ON the UI thread (guaranteed by the InvokeAsync call above — never call this directly
    // from a background thread). Owner = the main window only while it's actually VISIBLE
    // (IsVisible, decompile-verified: Window.Show()/Hide() toggle exactly this) — a hide-to-tray
    // stop must still surface the prompt, so it shows standalone and pulls itself forward instead
    // of silently attaching to a window nobody can see.
    Task<bool> ShowConfirmForceStopDialogAsync(string label) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = BuildConfirmForceStopWindow(label, tcs);

        if (_coordinator?.Window is { IsVisible: true } owner) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // Plain code-built Window (same style as BuildStartupErrorWindow above) rather than a XAML
    // view — this dialog has no ViewModel, no data binding, and exists only to resolve `tcs`.
    // "Stop anyway" is IsDefault (Enter-triggered, styled as the destructive default per spec);
    // "Cancel" is IsCancel (Esc-triggered). Closing via the titlebar/Esc without clicking either
    // button also resolves false — TrySetResult is idempotent, so whichever path runs first wins
    // and the other is a no-op.
    internal static Window BuildConfirmForceStopWindow(string label, TaskCompletionSource<bool> tcs) {
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        var stopButton = new Button {
            Content = "Stop anyway",
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#D32F2F")),
            Foreground = Brushes.White,
        };

        var window = new Window {
            Title = "Stop review participant?",
            Icon = ProductIcon.WindowIcon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = {
                    new TextBlock {
                        Text = $"{label} is a review participant. Stopping it will strand its flow. Stop anyway?",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, stopButton },
                    },
                },
            },
        };

        stopButton.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        return window;
    }

    // Async-safe shutdown: ShutdownRequested fires on the UI thread and can be cancelled, so the
    // FIRST pass defers it (e.Cancel = true), cancels the shutdown token (abandoning any
    // in-flight StartDaemonAsync WAIT — never the spawned daemon), and disposes the service in
    // the background (no live socket read/child-process wait may survive app exit, spec §5).
    // Once that completes, TryShutdown() re-raises this same event; the SECOND pass is let
    // through. This never blocks the UI thread on the async disposal.
    void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) {
        if (!BeginShutdownPass(_coordinator, _shutdownConfirmed)) return;

        e.Cancel = true;
        _shutdown.Cancel();
        if (_shutdownStarted) return; // e.g. a rapid double Cmd+Q — disposal is already in flight
        _shutdownStarted = true;
        _ = DisposeAndShutdownAsync();
    }

    // Split out of OnShutdownRequested so a test can drive BOTH passes (the event itself needs a
    // live App and a real lifetime, over a composition that needs a real daemon). Two rules, in
    // this order:
    //
    // 1. QuitInProgress is flagged on EVERY pass — including the confirmed one, which is why this
    //    runs before the guard below. A coordinator that only comes into existence BETWEEN the
    //    passes (quit or an OS logout arriving while CreateDefaultAsync is still in flight, with
    //    StartAsync's continuation then building the window during the deferred disposal's await)
    //    would otherwise still have hide-on-close armed when the second pass closes the windows:
    //    the window cancels its own close, DoShutdown aborts with windows still open, and every
    //    later quit early-returns on _shutdownConfirmed — an app that can only be force-quit.
    //    Setting it again on a pass that already set it is a no-op.
    // 2. The confirmed (second) pass is let through untouched — no e.Cancel — which is what the
    //    caller's early return preserves.
    internal static bool BeginShutdownPass(MainWindowCoordinator? coordinator, bool shutdownConfirmed) {
        if (coordinator is not null) coordinator.QuitInProgress = true;
        return !shutdownConfirmed;
    }

    async Task DisposeAndShutdownAsync() {
        // AI-1654 §3.6: mutations are never abandoned — give a lifecycle-controller-triggered
        // mutation (startup matrix, skew, txn-requery; none of these carry _shutdown.Token) a
        // bounded chance to finish naturally, WHILE the UI is still up, before anything below
        // starts tearing it down.
        if (_lifecycle is not null) await AwaitQuiescedAsync(_lifecycle.QuiescedAsync, QuiesceShutdownCap).ConfigureAwait(false);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // Prompt coordinator BEFORE the consent service (spec §5): the window and its
            // ViewModel are gone before the service they resolve against, so no click can reach a
            // disposed one. A resolve already in flight was cancelled by _shutdown at the top of
            // OnShutdownRequested and settles on the ViewModel's silent-abort path.
            await DisposeUiThenConfirmShutdownAsync(
                [_tray, _trayVm, _promptCoordinator, _consent, _activity, _pause],
                DisposeLifecycleAndServiceAsync, () => _shutdownConfirmed = true, desktop, _exitCode);
        } else {
            await DisposeLifecycleAndServiceAsync();
            _shutdownConfirmed = true;
        }
    }

    // The dependent (_lifecycle, subscribed to _service's streams) goes first — same rule as
    // BuildLifecycleController's construction-order comment, in reverse. A throw disposing it must
    // never skip _service's own disposal, so it gets its own guard rather than sharing the outer
    // DisposeAndConfirmShutdownAsync's single try/catch.
    async ValueTask DisposeLifecycleAndServiceAsync() {
        if (_lifecycle is not null) {
            try {
                await _lifecycle.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during shutdown: {ex}");
            }
        }
        if (_service is not null) await _service.DisposeAsync().ConfigureAwait(false);
    }

    // §3.6's cap: QuiescedAsync itself is unbounded (it just waits for the gate), so this is what
    // keeps a stuck internal mutation from hanging shutdown forever — DisposeAsync's own eventual
    // lifetime-cancel is still the backstop if the cap is reached. Static + delegate-shaped so a
    // test can drive it without a live controller.
    internal static async Task AwaitQuiescedAsync(Func<Task> quiescedAsync, TimeSpan cap) {
        await Task.WhenAny(quiescedAsync(), Task.Delay(cap)).ConfigureAwait(false);
    }

    // Split out of DisposeAndShutdownAsync so a test can pin the ordering with a recording list.
    // The UI-thread-owned disposables go first, synchronously on the UI thread this runs on (the
    // ShutdownRequested thread), so the menu-bar icon is gone before TryShutdown (spec §9) — then
    // the deferred pass below proceeds exactly as it did before the tray existed.
    internal static Task DisposeUiThenConfirmShutdownAsync(
            IReadOnlyList<IDisposable?> uiDisposables, Func<ValueTask>? disposeAsync, Action markConfirmed,
            IClassicDesktopStyleApplicationLifetime desktop, int exitCode) {
        DisposeAll(uiDisposables, "shutdown");
        return DisposeAndConfirmShutdownAsync(disposeAsync, markConfirmed, desktop, exitCode);
    }

    // Per-entry guard for the same reason DisposeAndConfirmShutdownAsync wraps its disposeAsync: a
    // throw here must never skip the remaining disposables, markConfirmed or TryShutdown —
    // _shutdownConfirmed would stay false while _shutdownStarted stayed true, cancelling every
    // later quit forever. Null entries are the "that step never ran" case.
    static void DisposeAll(IReadOnlyList<IDisposable?> disposables, string phase) {
        foreach (var disposable in disposables) {
            try {
                disposable?.Dispose();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose a UI service during {phase}: {ex}");
            }
        }
    }

    // Split out of DisposeAndShutdownAsync so a test can drive the full deferred-shutdown pass —
    // dispose, THEN mark confirmed, THEN shut down carrying an exit code — against a fake
    // IClassicDesktopStyleApplicationLifetime, without needing a live App instance.
    // `disposeAsync` is a delegate (not the concrete DaemonClientService) so a test can inject a
    // throwing disposal without depending on how DaemonClientService itself might fail.
    // Regression coverage for a P2 bug found in re-review: TryShutdown() used to be called with
    // no exit code (defaulting to 0), so Cmd+Q/platform shutdown while the startup-error window
    // was still showing silently overwrote the startup-failure exit code with success. Ordering
    // is preserved exactly from the original inline body: `markConfirmed` MUST run before
    // `TryShutdown`, because TryShutdown can re-raise ShutdownRequested synchronously and
    // OnShutdownRequested's early-return guard (`if (_shutdownConfirmed) return;`) depends on
    // that happening first.
    internal static async Task DisposeAndConfirmShutdownAsync(
            Func<ValueTask>? disposeAsync, Action markConfirmed, IClassicDesktopStyleApplicationLifetime desktop,
            int exitCode) {
        // A throwing disposeAsync must never skip markConfirmed/TryShutdown — otherwise
        // _shutdownConfirmed is never set while _shutdownStarted stays true, and every later
        // quit is cancelled forever.
        try {
            if (disposeAsync is not null) await disposeAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during shutdown: {ex}");
        } finally {
            markConfirmed();
            desktop.TryShutdown(exitCode);
        }
    }
}
