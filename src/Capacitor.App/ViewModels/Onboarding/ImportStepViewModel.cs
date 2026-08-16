using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One vendor checkbox on the Import step. Unlike plugin-install, `kcap import` has no flagless
/// Claude default — Claude's own selector is the explicit `--claude` flag.
public sealed class ImportVendorRow : ReactiveObject {
    readonly Func<AgentDetectionResult, DetectedAgent> _select;

    internal ImportVendorRow(AgentVendor vendor) {
        Label   = vendor.Label;
        Flag    = vendor.Flag ?? "--claude";
        _select = vendor.Select;
    }

    public string Label { get; }
    internal string Flag { get; }

    bool _isSelected;
    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    internal bool DetectedIn(AgentDetectionResult result) => _select(result).Detected;
}

/// spec §3 step 6 / decision 6: scope + vendor selection, streamed `kcap import` into a bounded
/// log pane. A completed run's exit code never blocks Next — only leaving mid-run kills it (§7).
public sealed class ImportStepViewModel : ReactiveObject, IWizardStep {
    internal const int    LogLimit  = 500;
    internal const string RetryHint = "If anything failed, run `kcap import` in a terminal to retry.";

    readonly IKcapCli _cli;
    readonly Func<CancellationToken, Task<AgentDetectionResult>> _detect;
    readonly Action<Action> _post;

    AgentDetectionResult?    _detected;
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
            IKcapCli cli, Func<CancellationToken, Task<AgentDetectionResult>> detect, Action<Action> post) {
        _cli    = cli;
        _detect = detect;
        _post   = post;

        Vendors = AgentVendors.All.Select(v => new ImportVendorRow(v)).ToList();

        var idle = this.WhenAnyValue(x => x.Busy, busy => !busy);
        var scopeValid = this.WhenAnyValue(x => x.Scope, x => x.OrgText, x => x.RepoText,
            (scope, org, repo) => scope switch {
                ImportScopeChoice.Everything => true,
                ImportScopeChoice.Org        => !string.IsNullOrWhiteSpace(org),
                ImportScopeChoice.Repo       => !string.IsNullOrWhiteSpace(repo),
                _                            => false,
            });
        var canRun = idle.CombineLatest(scopeValid, (i, v) => i && v && CliAvailable);

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

        AgentDetectionResult detected;
        try { detected = await _detect(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _detected = detected;
        foreach (var row in Vendors) row.IsSelected = row.DetectedIn(detected);
    }

    /// A running import is always killed, not abandoned: §7's Cancel/close contract applies to
    /// leaving the step too, so no import survives the wizard moving on.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        _cts?.Cancel();

        if (_run is { } run) {
            try { await run.ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard import run failed unexpectedly: {ex.Message}"); }
        }

        return true;
    }

    internal Task RunAsync() {
        if (Busy) return Task.CompletedTask;
        return _run = RunCoreAsync();
    }

    async Task RunCoreAsync() {
        if (!CliAvailable) return;

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
        } finally {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

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
