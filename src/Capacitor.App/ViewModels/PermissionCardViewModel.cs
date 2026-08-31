using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One NEEDS YOU card. Detail follows the chat tab's root, which the agent stream delivers
/// independently of the permission replay, so a card built first re-renders relative later.
public sealed class PermissionCardViewModel : PendingCardViewModel {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly ObservableAsPropertyHelper<string> _detail;

    public string ToolName { get; }
    public string Detail => _detail.Value;
    public bool ShowsAllowAlways { get; }

    public ReactiveCommand<Unit, Unit> AllowCommand { get; }
    public ReactiveCommand<Unit, Unit> AllowAlwaysCommand { get; }
    public ReactiveCommand<Unit, Unit> DenyCommand { get; }

    public PermissionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions, IObservable<string?> root) : base(entry) {
        _entry = entry;
        _permissions = permissions;
        ToolName = entry.ToolName.Length == 0 ? "Tool call" : entry.ToolName;
        ShowsAllowAlways = entry.Vendor == "claude" && entry.ToolName != ClaudeElicitation.ToolName;

        _detail = root
            .Select(r => entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, r))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Detail, entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, null))
            .DisposeWith(Disposables);

        var idle = Busy.Select(b => !b);
        AllowCommand       = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Allow), idle);
        AllowAlwaysCommand = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.AllowAlways), idle);
        DenyCommand        = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Deny), idle);
        Disposables.Add(AllowCommand);
        Disposables.Add(AllowAlwaysCommand);
        Disposables.Add(DenyCommand);
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
}
