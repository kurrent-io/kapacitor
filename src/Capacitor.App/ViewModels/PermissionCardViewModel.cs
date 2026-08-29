using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One NEEDS YOU card. Detail follows the chat tab's root, which the agent stream delivers
/// independently of the permission replay, so a card built first re-renders relative later.
public sealed class PermissionCardViewModel : ReactiveObject, IDisposable {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<string> _detail;

    public string RequestId => _entry.RequestId;
    public string ToolName { get; }
    public string Detail => _detail.Value;
    public bool ShowsAllowAlways { get; }

    /// Sortable ISO text — the tie-break key ChatTabViewModel's card comparer orders by.
    internal string RequestedAtKey => _entry.RequestedAt.ToString("O");

    // Feeds AllowCommand/AllowAlwaysCommand/DenyCommand's canExecute via this subject rather than
    // this.WhenAnyValue: that call routes through ReactiveUI's ObservableForProperty/RxAppBuilder
    // global init, which is only reliably primed when some other test has already pumped the
    // headless dispatcher first (SessionRailViewModel's own reason for the same substitution).
    readonly BehaviorSubject<bool> _busy = new(false);

    bool _isBusy;
    public bool IsBusy {
        get => _isBusy;
        private set {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            _busy.OnNext(value);
        }
    }

    string? _errorText;
    public string? ErrorText { get => _errorText; private set => this.RaiseAndSetIfChanged(ref _errorText, value); }

    public ReactiveCommand<Unit, Unit> AllowCommand { get; }
    public ReactiveCommand<Unit, Unit> AllowAlwaysCommand { get; }
    public ReactiveCommand<Unit, Unit> DenyCommand { get; }

    public PermissionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions, IObservable<string?> root) {
        _entry = entry;
        _permissions = permissions;
        ToolName = entry.ToolName.Length == 0 ? "Tool call" : entry.ToolName;
        ShowsAllowAlways = entry.Vendor == "claude";

        _detail = root
            .Select(r => entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, r))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Detail, entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, null))
            .DisposeWith(_disposables);

        var idle = _busy.Select(b => !b);
        AllowCommand       = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Allow), idle);
        AllowAlwaysCommand = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.AllowAlways), idle);
        DenyCommand        = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Deny), idle);
        _disposables.Add(AllowCommand);
        _disposables.Add(AllowAlwaysCommand);
        _disposables.Add(DenyCommand);
        _disposables.Add(_busy);
    }

    async Task AnswerAsync(PermissionAnswer answer) {
        IsBusy = true;
        ErrorText = null;
        try {
            var outcome = await _permissions.ResolveAsync(_entry, answer, CancellationToken.None);
            if (outcome.Kind == PermissionResolveKind.TransportFailure)
                ErrorText = outcome.Error == "daemon_unreachable" ? "Daemon unreachable — try again" : $"Could not answer ({outcome.Error}) — try again";
        } finally {
            IsBusy = false;
        }
    }

    public void Dispose() => _disposables.Dispose();
}
