using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per row revision (DynamicData Transform), so every
/// static field is computed once from the ctor row; IsSelected and NeedsYou stay live because
/// selection and pending-set membership each change without a row revision. Age is a
/// point-in-time snapshot (SessionCardViewModel precedent).
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    public string Primary { get; }
    public string Sub { get; }
    public IBrush StatusDot { get; }
    public string Tooltip { get; }
    /// The daemon name badge for a remote row; null for a local one.
    public string? MachineBadge { get; }
    public bool IsRemote { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedAgentId,
            IObservable<IReadOnlySet<string>> agentsWithPending,
            Action<string> openLocal, Action<string> openRemoteInWeb) {
        Id = row.Id;
        CreatedAt = row.CreatedAt;
        var kindLine = row.Kind == "agent" ? row.Vendor : $"{row.Vendor} · {row.Kind}";
        var vendorLine = row.WorkLocation == WorkLocationText.Borrowed ? $"{kindLine} · borrowed" : kindLine;
        var age = UptimeFormat.Format(DateTime.UtcNow - DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));

        Primary = row.Title ?? vendorLine;
        Sub = row.Title is null
            ? Join(row.Model, age)
            : Join(vendorLine, row.Model, age);
        StatusDot = SessionStatusDots.For(row.Status);
        Tooltip = Join(row.Id, row.Status, row.RequesterDisplay, row.BorrowedFrom is null ? null : $"borrowed {row.BorrowedFrom}");
        MachineBadge = row.MachineBadge;
        IsRemote = row.Origin == AgentOrigin.Remote;

        _isSelected = selectedAgentId.Select(sel => sel == row.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var byStatus = SessionStatusDots.NeedsAttention(row.Status);
        _needsYou = agentsWithPending.Select(set => byStatus || set.Contains(row.Id))
            .ToProperty(this, x => x.NeedsYou, initialValue: byStatus)
            .DisposeWith(_disposables);

        // Slice 1 remote rows are read-only in-app: opening one deep-links to the web instead of
        // routing into the local workspace.
        OpenCommand = ReactiveCommand.Create(() => (IsRemote ? openRemoteInWeb : openLocal)(row.Id));
        _disposables.Add(OpenCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
