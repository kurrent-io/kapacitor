using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per dto revision (DynamicData Transform), so every
/// static field is computed once from the ctor dto; IsSelected and NeedsYou stay live because
/// selection and pending-set membership each change without a dto revision. Age is a
/// point-in-time snapshot (SessionCardViewModel precedent).
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    public string Primary { get; }
    public string Sub { get; }
    public IBrush StatusDot { get; }
    public string Tooltip { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(
            AgentStatusDto dto, IObservable<string?> selectedAgentId,
            IObservable<IReadOnlySet<string>> agentsWithPending, Action<string> open) {
        Id = dto.Id;
        CreatedAt = dto.CreatedAt;
        var kindLine = dto.Kind == "agent" ? dto.Vendor : $"{dto.Vendor} · {dto.Kind}";
        var vendorLine = dto.WorkLocation == WorkLocationText.Borrowed ? $"{kindLine} · borrowed" : kindLine;
        var age = UptimeFormat.Format(DateTime.UtcNow - DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc));

        Primary = dto.Title ?? vendorLine;
        Sub = dto.Title is null
            ? Join(dto.Model, age)
            : Join(vendorLine, dto.Model, age);
        StatusDot = SessionStatusDots.For(dto.Status);
        Tooltip = Join(dto.Id, dto.Status, SessionStatusDots.WaitsOnUser(dto) ? "waiting for input" : null,
            dto.RequesterDisplay, dto.BorrowedFrom is null ? null : $"borrowed {dto.BorrowedFrom}");

        _isSelected = selectedAgentId.Select(sel => sel == dto.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var byStatus = SessionStatusDots.NeedsAttention(dto);
        _needsYou = agentsWithPending.Select(set => byStatus || set.Contains(dto.Id))
            .ToProperty(this, x => x.NeedsYou, initialValue: byStatus)
            .DisposeWith(_disposables);

        OpenCommand = ReactiveCommand.Create(() => open(dto.Id));
        _disposables.Add(OpenCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
