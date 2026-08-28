using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One vendor checkbox on the Import step. Unlike plugin-install, `kcap import` has no flagless
/// Claude default — Claude's own selector is the explicit `--claude` flag.
public sealed class ImportVendorRow : ReactiveObject {
    readonly HarnessId _id;

    internal ImportVendorRow(AgentVendor vendor) {
        Label = vendor.Label;
        Flag  = vendor.Id.Flag;
        _id   = vendor.Id;
    }

    public string Label { get; }
    internal string Flag { get; }

    bool _isSelected;
    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    internal bool DetectedIn(IReadOnlySet<HarnessId> detected) => detected.Contains(_id);
}

/// Scope + vendor selection, streamed `kcap import` into a bounded log pane. A completed run's exit
/// code never blocks Next — only leaving mid-run kills it.
public sealed class ImportStepViewModel : ReactiveObject, IWizardStep {
    internal const int    LogLimit  = 500;
    internal const string RetryHint = "If anything failed, run `kcap import` in a terminal to retry.";

    readonly IKcapCli _cli;
    readonly Func<CancellationToken, Task<IReadOnlySet<HarnessId>>> _detect;
    readonly Action<Action> _post;

    IReadOnlySet<HarnessId>? _detected;
    CancellationTokenSource? _cts;
    Task?                    _run;

    ImportScopeChoice _scope = ImportScopeChoice.Everything;
    string  _orgText = "";
    string  _repoText = "";
    bool    _busy;
    bool    _satisfied;
    bool    _truncated;
    string? _status;

    public ImportStepViewModel(
            IKcapCli cli, Func<CancellationToken, Task<IReadOnlySet<HarnessId>>> detect, Action<Action> post) {
        _cli    = cli;
        _detect = detect;
        _post   = post;

        Vendors = AgentVendors.All.Select(v => new ImportVendorRow(v)).ToList();

        var idle        = this.WhenAnyValue(x => x.Busy, busy => !busy);
        var scopeValid  = this.WhenAnyValue(x => x.Scope, x => x.OrgText, x => x.RepoText, (_, _, _) => IsScopeValid());
        var anySelected = Vendors.Select(v => v.WhenAnyValue(x => x.IsSelected)).CombineLatest()
            .Select(flags => flags.Any(selected => selected));
        var canRun = Observable.CombineLatest(idle, scopeValid, anySelected, (i, v, a) => i && v && a && CliAvailable);

        RunCommand    = ReactiveCommand.CreateFromTask(RunAsync, canRun);
        CancelCommand = ReactiveCommand.Create(() => _cts?.Cancel());
    }

    public WizardStepId Id         => WizardStepId.Import;
    public string       Title      => "Import past sessions";
    public bool         Applicable => true;

    public IReadOnlyList<ImportVendorRow> Vendors { get; }

    public ImportScopeChoice Scope {
        get => _scope;
        set {
            this.RaiseAndSetIfChanged(ref _scope, value);
            RestateScope();
        }
    }

    public bool EverythingSelected {
        get => Scope == ImportScopeChoice.Everything;
        set { if (value) Scope = ImportScopeChoice.Everything; }
    }

    public bool OrgSelected {
        get => Scope == ImportScopeChoice.Org;
        set { if (value) Scope = ImportScopeChoice.Org; }
    }

    public bool RepoSelected {
        get => Scope == ImportScopeChoice.Repo;
        set { if (value) Scope = ImportScopeChoice.Repo; }
    }

    public string OrgText {
        get => _orgText;
        set => this.RaiseAndSetIfChanged(ref _orgText, value);
    }

    public string RepoText {
        get => _repoText;
        set => this.RaiseAndSetIfChanged(ref _repoText, value);
    }

    public bool CliAvailable => _cli.CliPath is not null;

    public string? Message => CliAvailable ? null : "kcap CLI not found";

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public string? Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// Set once the log has dropped its first line — the pane shows an "older lines dropped"
    /// header for the rest of the run instead of pretending the tail is the whole transcript.
    public bool Truncated {
        get => _truncated;
        private set => this.RaiseAndSetIfChanged(ref _truncated, value);
    }

    public ObservableCollection<string> Log { get; } = [];

    public ReactiveCommand<Unit, Unit> RunCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public async Task OnEnterAsync(CancellationToken ct) {
        if (_detected is not null) return; // cached: re-entering the step must not stomp user choices

        IReadOnlySet<HarnessId> detected;
        try { detected = await _detect(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _detected = detected;
        foreach (var row in Vendors) row.IsSelected = row.DetectedIn(detected);
    }

    /// A running import is always killed, not abandoned: §7's Cancel/close contract applies to
    /// leaving the step too, so no import survives the wizard moving on.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        await CancelActiveRunAsync().ConfigureAwait(false);

        return true;
    }

    /// The other half of §7's Cancel/close contract: closing the wizard window never navigates away
    /// from a step, so it cannot go through <see cref="CanLeaveAsync"/> — the app's close/shutdown
    /// paths call this directly instead. No-op when idle; never throws.
    public async Task CancelActiveRunAsync() {
        _cts?.Cancel();

        if (_run is { } run) {
            try { await run.ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard import run failed unexpectedly: {ex.Message}"); }
        }
    }

    internal Task RunAsync() {
        if (Busy) return Task.CompletedTask;
        return _run = RunCoreAsync();
    }

    async Task RunCoreAsync() {
        // Mirrors canRun's gating — a direct call (tests, or a future non-button trigger) must not
        // bypass any of it.
        if (!CliAvailable) return;
        if (!IsScopeValid()) return;
        if (!Vendors.Any(v => v.IsSelected)) return; // empty VendorFlags means "import everything" to the CLI — never silently do that

        Log.Clear();
        Truncated = false;
        Status    = null;
        Busy      = true;
        _cts      = new CancellationTokenSource();

        var request = new ImportRequest(
            Scope,
            OrgOrRepoValue(),
            Vendors.Where(v => v.IsSelected).Select(v => v.Flag).ToArray());

        try {
            var result = await _cli.ImportAsync(request, OnLine, _cts.Token).ConfigureAwait(false);
            Satisfied = result.ExitCode == 0;
            Status    = (result.ExitCode == 0 ? "Import complete." : $"Import finished with errors (exit {result.ExitCode}).")
                        + " " + RetryHint;
        } catch (OperationCanceledException) {
            Status = "Import cancelled."; // the user chose to stop — no retry hint, unlike a real failure
        } catch (Exception ex) {
            Satisfied = false;
            Status    = $"Import failed: {ex.Message} {RetryHint}";
        } finally {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    bool IsScopeValid() => Scope switch {
        ImportScopeChoice.Everything => true,
        ImportScopeChoice.Org        => !string.IsNullOrWhiteSpace(OrgText),
        ImportScopeChoice.Repo       => !string.IsNullOrWhiteSpace(RepoText),
        _                            => false,
    };

    string? OrgOrRepoValue() => Scope switch {
        ImportScopeChoice.Org  => OrgText,
        ImportScopeChoice.Repo => RepoText,
        _                      => null,
    };

    void OnLine(StreamedLine line) => _post(() => {
        Log.Add(line.Text);
        if (Log.Count > LogLimit) {
            Log.RemoveAt(0);
            Truncated = true;
        }
    });

    void RestateScope() {
        this.RaisePropertyChanged(nameof(EverythingSelected));
        this.RaisePropertyChanged(nameof(OrgSelected));
        this.RaisePropertyChanged(nameof(RepoSelected));
    }
}
