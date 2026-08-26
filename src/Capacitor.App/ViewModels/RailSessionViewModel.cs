using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per dto revision (DynamicData Transform), so every
/// static field is computed once from the ctor dto; only IsSelected is live — selection changes
/// are not dto revisions. Age is a point-in-time snapshot (SessionCardViewModel precedent).
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    public string Primary { get; }
    public string Sub { get; }
    public IBrush StatusDot { get; }
    public bool NeedsYou { get; }
    public string Tooltip { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(AgentStatusDto dto, IObservable<string?> selectedAgentId, Action<string> open) {
        Id = dto.Id;
        CreatedAt = dto.CreatedAt;
        var vendorLine = dto.Kind == "agent" ? dto.Vendor : $"{dto.Vendor} · {dto.Kind}";
        var age = UptimeFormat.Format(DateTime.UtcNow - DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc));

        Primary = dto.Title ?? vendorLine;
        Sub = dto.Title is null
            ? Join(dto.Model, age)
            : Join(vendorLine, dto.Model, age);
        StatusDot = SessionStatusDots.For(dto.Status);
        NeedsYou = SessionStatusDots.NeedsAttention(dto.Status);
        Tooltip = Join(dto.Id, dto.Status, dto.RequesterDisplay);

        _isSelected = selectedAgentId.Select(sel => sel == dto.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);
        OpenCommand = ReactiveCommand.Create(() => open(dto.Id));
        _disposables.Add(OpenCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
