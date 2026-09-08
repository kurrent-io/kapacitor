using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// spec §3 step 1: the PATH shim. Reuses PathShimInstaller as-is (AppleScript sudo, non-forcing
/// symlink, post-install re-probe) and claims ShimOffered so the post-wizard ShimOfferCoordinator
/// never re-offers this machine.
public sealed class ShimStepViewModel : ReactiveObject, IWizardStep {
    readonly PathShimInstaller _installer;
    readonly IAppStateStore    _store;
    readonly string?           _target;
    readonly string            _destination;

    bool    _offerClaimed;
    bool    _busy;
    bool    _satisfied;
    string? _message;

    public ShimStepViewModel(bool applicable, PathShimInstaller installer, IAppStateStore store, string? target)
        : this(applicable, installer, store, target, PathShimInstaller.Destination) { }

    // Test seam mirroring ShimOfferCoordinator's own destination-override constructor (real filesystem taxonomy against a temp path, never the real /usr/local/bin/kcap).
    internal ShimStepViewModel(
            bool applicable, PathShimInstaller installer, IAppStateStore store, string? target, string destination) {
        Applicable   = applicable;
        _installer   = installer;
        _store       = store;
        _target      = target;
        _destination = destination;

        InstallCommand = ReactiveCommand.CreateFromTask(RunInstallAsync, this.WhenAnyValue(x => x.Idle));
    }

    /// Pure decision (spec §3 step 1): macOS AND a resolved absolute CLI path AND the login-shell
    /// probe positively found no kcap on the terminal PATH. A null (unknown) probe fails quiet —
    /// never offer on an inconclusive read. Called by the composition root with a pre-probed
    /// value, since Applicable is sync and the probe is async.
    public static bool ComputeApplicable(bool isMacOs, string? target, bool? kcapOnPath) =>
        isMacOs && target is not null && kcapOnPath == false;

    public WizardStepId Id         => WizardStepId.Shim;
    public string       Title      => "Command-line tool";
    public bool         Applicable { get; }

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    /// Set on InstalledButNotOnPath/Failed (with recovery guidance); null on Installed/Cancelled.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public ReactiveCommand<Unit, Unit> InstallCommand { get; }

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    // Never vetoes: an unresolved or failed shim link must not block the rest of setup.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);

    async Task RunInstallAsync() {
        if (_target is null) {
            Message = "kcap CLI not found";
            return;
        }

        Busy = true;
        try {
            await ClaimOfferedOnceAsync().ConfigureAwait(false);
            Apply(await _installer.InstallAsync(_target, _destination, CancellationToken.None).ConfigureAwait(false));
        } finally {
            Busy = false;
        }
    }

    // Claim-before-install (mirrors ShimOfferCoordinator): persisted once, before the outcome is known, so a retry click never re-persists.
    Task ClaimOfferedOnceAsync() {
        if (_offerClaimed) return Task.CompletedTask;
        _offerClaimed = true;
        return _store.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true });
    }

    void Apply(ShimResult result) {
        switch (result.Outcome) {
            case ShimOutcome.Installed:
                Satisfied = true;
                Message   = null;
                break;
            case ShimOutcome.InstalledButNotOnPath:
                Satisfied = false;
                Message   = result.Detail;
                break;
            case ShimOutcome.Cancelled:
                Satisfied = false;
                Message   = null; // the user knows they cancelled it — nothing more to say
                break;
            default: // Failed
                Satisfied = false;
                Message = result.SudoFallback is null
                    ? result.Detail ?? "Installing the command-line tool failed."
                    : $"{result.Detail} Or run: {result.SudoFallback}";
                break;
        }
    }
}
