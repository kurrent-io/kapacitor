using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// spec §5: the once-ever shim offer + the "Install command-line tool…" tray item it shares a
/// code path with. Waits for the SAME startup-phase signal the lifecycle controller exposes
/// (`PhaseClosed`, any path — an immediate `Connected` releases this exactly like a completed
/// `daemon_unreachable` branch) before probing anything, so the offer dialog can never race the
/// startup matrix. `ConfirmAsync` is routed through the SAME `ILifecycleSurface` the skew/repair
/// dialogs use — its own `SemaphoreSlim(1,1)` already guarantees this offer never stacks over one
/// of those, so no second serialization lives here.
///
/// spec §3.3: macOS-only, no-op elsewhere — off-macOS the target is forced null at construction,
/// so this degrades exactly like "nothing to link" everywhere below.
public sealed class ShimOfferCoordinator {
    internal const string ShimDisclosure =
        "This links /usr/local/bin/kcap to this app's CLI, so kcap works from any terminal. " +
        "Installing it prompts once for your admin password.";

    readonly Task _phaseClosed;
    readonly ILoginShellProbe _probe;
    readonly PathShimInstaller _installer;
    readonly IAppStateStore _store;
    readonly ILifecycleSurface _surface;
    readonly string? _target;
    readonly CancellationToken _lifetime;
    readonly string _destination;
    // Task 15 round-1 review: true only for a gate-Incomplete startup — Offerable/manual install
    // still work, only the once-ever auto-offer DIALOG is skipped.
    readonly bool _autoOfferSuppressed;

    readonly BehaviorSubject<bool> _offerable = new(false);

    /// <param name="target">
    /// The resolved ABSOLUTE CLI path (CliResolver), or null when only the bare "kcap" command
    /// name resolved — there is nothing to link, so the offer and the menu item both stay off for
    /// the whole run.
    /// </param>
    public ShimOfferCoordinator(
            Task phaseClosed, ILoginShellProbe probe, PathShimInstaller installer, IAppStateStore store,
            ILifecycleSurface surface, string? target, CancellationToken lifetime, bool autoOfferSuppressed = false)
        : this(phaseClosed, probe, installer, store, surface, target, lifetime, PathShimInstaller.Destination, autoOfferSuppressed) { }

    // Test seam mirroring PathShimInstaller.InstallAsync's own internal destination-override
    // overload: lets tests drive real Preflight/InstallAsync taxonomy against a temp path instead
    // of the real /usr/local/bin/kcap. Production always goes through the public constructor
    // above, which pins `destination` to PathShimInstaller.Destination.
    internal ShimOfferCoordinator(
            Task phaseClosed, ILoginShellProbe probe, PathShimInstaller installer, IAppStateStore store,
            ILifecycleSurface surface, string? target, CancellationToken lifetime, string destination,
            bool autoOfferSuppressed = false)
        : this(phaseClosed, probe, installer, store, surface, target, lifetime, destination, OperatingSystem.IsMacOS, autoOfferSuppressed) { }

    // spec §3.3: macOS-only, no-op elsewhere. `isMacOs` is a test seam (off-macOS can't otherwise
    // be exercised from macOS CI); production always resolves to the real OS check. Nulling
    // `_target` off-macOS reuses every existing "nothing to link" no-op path below (RunAsync,
    // RunInstallAsync) instead of adding a second guard.
    internal ShimOfferCoordinator(
            Task phaseClosed, ILoginShellProbe probe, PathShimInstaller installer, IAppStateStore store,
            ILifecycleSurface surface, string? target, CancellationToken lifetime, string destination,
            Func<bool> isMacOs, bool autoOfferSuppressed = false) {
        _phaseClosed          = phaseClosed;
        _probe                = probe;
        _installer            = installer;
        _store                = store;
        _surface              = surface;
        _target               = isMacOs() ? target : null;
        _lifetime             = lifetime;
        _destination          = destination;
        _autoOfferSuppressed  = autoOfferSuppressed;
    }

    /// True while the tray's "Install command-line tool…" item should show: applicable (an
    /// absolute link target exists) but absent (the probe positively found no `kcap` on the
    /// terminal PATH) — independent of whether the once-ever auto-offer itself ran.
    public IObservable<bool> Offerable => _offerable;

    /// Call once, after subscribing to `Offerable` — mirrors DaemonLifecycleController.Start's
    /// subscribe-before-run shape. Fire-and-forget: every path through RunAsync is exception-safe.
    public void Start() => _ = RunAsync();

    /// The tray menu item's click handler (spec §5): runs the SAME install path as an accepted
    /// offer, regardless of whether this run already offered or the user already declined — a
    /// manual retry is always allowed.
    public Task RunManualInstallAsync() => RunInstallAsync();

    async Task RunAsync() {
        try {
            await _phaseClosed.ConfigureAwait(false);
            if (_target is null) return; // nothing to link — offer and menu item both stay off

            var onPath = await _probe.KcapOnPathAsync(_lifetime).ConfigureAwait(false);
            if (onPath != false) return; // true (already on PATH) or unknown → never auto-offer, item stays hidden

            _offerable.OnNext(true); // applicable-but-absent — the menu item is live from here on

            if (_autoOfferSuppressed) return; // item + manual install still work; only the dialog never fires

            var state = await _store.LoadAsync().ConfigureAwait(false);
            if (state.ShimOffered || state.ShimDenied) return; // already resolved on a prior run

            var preflight = PathShimInstaller.Preflight(_destination, _target);
            if (preflight == ShimPreflight.Conflict) return; // no auto-offer; the menu item still lets the user try
            if (preflight == ShimPreflight.AlreadyInstalled) {
                await ClaimOfferedAsync().ConfigureAwait(false);
                return;
            }

            // §3.5 claim-before-show: persisted BEFORE ConfirmAsync so a crash while the dialog is
            // open still suppresses a re-offer next run. A persist failure still proceeds — this
            // run never re-checks AppState again, so there is nothing left here to re-offer.
            await ClaimOfferedAsync().ConfigureAwait(false);

            var prompt = new LifecyclePrompt(LifecyclePrompt.KindShim, null, null, false, ShimDisclosure);
            var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
            if (!accepted) {
                await ClaimDeniedAsync().ConfigureAwait(false);
                return;
            }

            await RunInstallAsync().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown before the offer completed
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: shim offer failed unexpectedly: {ex.Message}");
        }
    }

    async Task RunInstallAsync() {
        try {
            if (_target is null) return; // defensive — the menu item is never shown without one
            var result = await _installer.InstallAsync(_target, _destination, _lifetime).ConfigureAwait(false);
            await SurfaceResultAsync(result).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown mid-install
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: shim install failed unexpectedly: {ex.Message}");
        }
    }

    Task SurfaceResultAsync(ShimResult result) => result.Outcome switch {
        // Confirmed on-PATH: the item is no longer applicable-but-absent (Offerable's own
        // contract), on both the auto-offer accept path and the manual tray-install path — both
        // route through here. InstalledButNotOnPath deliberately does NOT reset it: kcap is still
        // absent from the terminal PATH, so the item stays offerable for a retry.
        ShimOutcome.Installed => InstalledStatus(),
        ShimOutcome.InstalledButNotOnPath => Status(result.Detail ?? "kcap was linked, but is not yet on your terminal PATH."),
        ShimOutcome.Cancelled => ClaimDeniedThenStatus("Installing the command-line tool was canceled."),
        ShimOutcome.Failed => Status(result.SudoFallback is null
            ? result.Detail ?? "Installing the command-line tool failed."
            : $"{result.Detail} Or run: {result.SudoFallback}"),
        _ => Task.CompletedTask,
    };

    Task InstalledStatus() {
        _offerable.OnNext(false);
        return Status("kcap is now on your terminal PATH.");
    }

    async Task ClaimDeniedThenStatus(string message) {
        await ClaimDeniedAsync().ConfigureAwait(false);
        await Status(message).ConfigureAwait(false);
    }

    Task Status(string message) {
        _surface.Status(message);
        return Task.CompletedTask;
    }

    Task<bool> ClaimOfferedAsync() => _store.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true });
    Task<bool> ClaimDeniedAsync()  => _store.UpdateAsync(s => s.ShimDenied  ? s : s with { ShimDenied  = true });
}
