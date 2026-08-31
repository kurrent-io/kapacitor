using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Shared shell of a NEEDS YOU card. Setters are no-ops after disposal: the pipeline disposes a
/// card the instant its entry leaves the cache, and an in-flight submit continuation must not
/// notify a removed card.
public abstract class PendingCardViewModel : ReactiveObject, IDisposable {
    // CA1051: both fields are the shared shell every card subclass wires its own commands and
    // disposables into; a property indirection here buys nothing.
#pragma warning disable CA1051
    protected readonly CompositeDisposable Disposables = new();
    // canExecute feed; a BehaviorSubject rather than WhenAnyValue for the reason
    // SessionRailViewModel documents (headless ReactiveUI init ordering).
    protected readonly BehaviorSubject<bool> Busy = new(false);
#pragma warning restore CA1051
    bool _isBusy;
    string? _errorText;

    public string RequestId { get; }
    internal DateTimeOffset RequestedAt { get; }
    protected bool IsDisposed { get; private set; }

    public bool IsBusy {
        get => _isBusy;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            Busy.OnNext(value);
        }
    }

    public string? ErrorText {
        get => _errorText;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _errorText, value);
        }
    }

    protected PendingCardViewModel(PendingPermissionRequest entry) {
        RequestId = entry.RequestId;
        RequestedAt = entry.RequestedAt;
        Disposables.Add(Busy);
    }

    public void Dispose() {
        IsDisposed = true;
        Disposables.Dispose();
        GC.SuppressFinalize(this);
    }
}
