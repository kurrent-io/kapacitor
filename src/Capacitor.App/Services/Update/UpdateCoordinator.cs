using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// Owns the update schedule and its UX: silent periodic checks, background download, one prompt
/// when a package is ready, and the hand-off to the updater at the very end of shutdown. Once a
/// package is ready nothing else is checked or downloaded this run — a later download, even a
/// failed one, deletes every other cached package.
public sealed class UpdateCoordinator {
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);
    internal const string CheckLabel = "Check for Updates…";
    internal const string ReadyDisclosure =
        "Restart Kurrent Capacitor to finish installing it. Running agents keep running; the daemon restarts on its own once it is idle.";

    readonly IAppUpdater _updater;
    readonly ILifecycleSurface _surface;
    readonly TimeProvider _time;
    readonly Action _quit;
    readonly CancellationToken _lifetime;
    readonly TimeSpan _initialDelay;
    readonly TimeSpan _interval;
    readonly BehaviorSubject<UpdateMenuItem> _menu;
    readonly Lock _lock = new();

    Task? _inflight;
    bool _manualPending;
    UpdateCandidate? _ready;
    UpdateCandidate? _pendingApply;

    /// The single-flight lane (`_inflight`) guarantees only one check ever writes this. Readers run
    /// on the UI thread via `RunMenuActionAsync`, and `RunCheckAsync` holds `_lock` across its
    /// synchronous prefix (through `_menu.OnNext` and into `ConfirmAsync`) — so this must never
    /// block on `_lock`, or a UI-thread caller blocked on the same dialog deadlocks against it.
    UpdateCandidate? Ready {
        get => Volatile.Read(ref _ready);
        set => Volatile.Write(ref _ready, value);
    }

    public UpdateCoordinator(
            IAppUpdater updater, ILifecycleSurface surface, TimeProvider time, Action quit, CancellationToken lifetime,
            TimeSpan? initialDelay = null, TimeSpan? interval = null) {
        _updater      = updater;
        _surface      = surface;
        _time         = time;
        _quit         = quit;
        _lifetime     = lifetime;
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _interval     = interval ?? DefaultInterval;
        _menu         = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(updater.IsAvailable, CheckLabel));
    }

    public IObservable<UpdateMenuItem> MenuItem => _menu.AsObservable();

    /// Applies a package left from a previous run, after the install-location guard and before any
    /// graph exists. Skipped on an update relaunch: a failed apply relaunches the old version with
    /// the same package still cached, and applying it again automatically would loop before any UI
    /// appeared. True means the process is being replaced.
    public static bool TryApplyPendingAtStartup(IAppUpdater updater, bool updateRelaunch) {
        if (!updater.IsAvailable || updateRelaunch) return false;
        if (updater.PendingRestart is not { } pending || !IsEligible(updater, pending)) return false;

        // A failed apply must fall through to the normal UI, where the schedule re-offers the
        // update visibly, rather than bricking every launch behind a repeating startup failure.
        try {
            updater.ApplyNow(pending);
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: applying the pending update failed: {ex.Message}");
            return false;
        }
    }

    internal static bool IsEligible(IAppUpdater updater, UpdateCandidate candidate) {
        var installed = updater.InstalledVersion;
        if (installed is null) return false;
        if (candidate.IsPrerelease && !IsPrerelease(installed)) return false;

        return PrereleaseSemver.IsNewer(candidate.Version, installed);
    }

    static bool IsPrerelease(string version) {
        var core = version.Split('+', 2)[0];
        return core.Contains('-');
    }

    public void Start() {
        if (!_updater.IsAvailable) return;
        _ = RunScheduleAsync();
    }

    /// The tray item's action: a check while idle, the restart once a package is ready.
    public Task RunMenuActionAsync() {
        if (Ready is { } ready) {
            RequestRestart(ready);
            return Task.CompletedTask;
        }

        return CheckAsync(manual: true);
    }

    /// Called by the shutdown sequence immediately before the platform shutdown call, so the
    /// updater's bounded wait only ever covers process exit.
    public void ApplyPendingOnExit() {
        UpdateCandidate? pending;
        lock (_lock) {
            pending = _pendingApply;
            _pendingApply = null;
        }
        if (pending is not null) _updater.ApplyOnExit(pending);
    }

    async Task RunScheduleAsync() {
        try {
            await Task.Delay(_initialDelay, _time, _lifetime).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested) {
                await CheckAsync(manual: false).ConfigureAwait(false);
                if (Ready is not null) return;
                await Task.Delay(_interval, _time, _lifetime).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // shutdown
        }
    }

    Task CheckAsync(bool manual) {
        lock (_lock) {
            if (_inflight is { IsCompleted: false } running) {
                if (manual) _manualPending = true;
                return running;
            }

            _inflight = RunCheckAsync(manual);
            return _inflight;
        }
    }

    async Task RunCheckAsync(bool manual) {
        try {
            if (Ready is { } ready) {
                if (manual) await ReportAsync($"Kurrent Capacitor {ready.Version} is ready to install.").ConfigureAwait(false);
                return;
            }

            var candidate = await _updater.CheckAsync(_lifetime).ConfigureAwait(false);
            if (candidate is null) {
                if (ConsumeManual(manual)) await ReportAsync($"You're up to date ({_updater.InstalledVersion}).").ConfigureAwait(false);
                return;
            }

            await _updater.DownloadAsync(candidate, null, _lifetime).ConfigureAwait(false);
            Ready = candidate;
            _menu.OnNext(new UpdateMenuItem(true, $"Restart to update to {candidate.Version}"));

            var prompt = new LifecyclePrompt(
                LifecyclePrompt.KindUpdateReady, candidate.Version, _updater.InstalledVersion, false,
                $"Kurrent Capacitor {candidate.Version} is ready. {ReadyDisclosure}");
            var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
            ConsumeManual(manual);
            if (accepted) RequestRestart(candidate);
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: update check failed: {ex.Message}");
            if (ConsumeManual(manual)) await ReportAsync("Could not check for updates. Try again later.").ConfigureAwait(false);
        }
    }

    bool ConsumeManual(bool manual) {
        lock (_lock) {
            var pending = _manualPending;
            _manualPending = false;
            return manual || pending;
        }
    }

    void RequestRestart(UpdateCandidate candidate) {
        lock (_lock) _pendingApply = candidate;
        _quit();
    }

    Task ReportAsync(string message) =>
        _surface.ConfirmAsync(new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, _updater.InstalledVersion, false, message), _lifetime);
}
