using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One grid row per AgentStatusDto revision (spec §8). The presentation fields are computed once
/// from the dto passed to the constructor — DynamicData's Transform recreates this whole object
/// (never mutates one in place) whenever the underlying dto changes, e.g. a Status transition, so
/// there is no "update in place" path to wire. Ticker/connected/stopsInFlight are pre-scheduled
/// by the caller (MainWindowViewModel) onto RxSchedulers.MainThreadScheduler — this class stays
/// scheduler-agnostic so a test can drive it with plain Subjects with no Avalonia session.
public sealed class AgentRowViewModel : ReactiveObject {
    public string Id { get; }
    public string Kind { get; }
    public string VendorDisplay { get; }
    public string RepoLeaf { get; }
    public string RepoFull { get; }
    public string Requester { get; }
    public string StatusText { get; }

    // Sort key only (spec §8: CreatedAt asc, Id ordinal tiebreak) — not part of the row's
    // presentation surface, so it stays internal rather than joining the pinned public members.
    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<string> _uptime;
    public string Uptime => _uptime.Value;

    readonly ObservableAsPropertyHelper<bool> _actionsEnabled;
    public bool ActionsEnabled => _actionsEnabled.Value;

    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }

    public AgentRowViewModel(
            AgentStatusDto dto, AgentActionService actions, IObservable<long> ticker, TimeProvider time,
            IObservable<bool> connected, IObservable<IReadOnlySet<string>> stopsInFlight) {
        Id = dto.Id;
        Kind = dto.Kind;
        VendorDisplay = dto.Model is null ? dto.Vendor : $"{dto.Vendor} ({dto.Model})";
        RepoLeaf = RepoLabel.Leaf(dto.RepoPath);
        RepoFull = dto.RepoPath ?? "";
        Requester = dto.Requester ?? "unknown";
        StatusText = dto.Status;
        CreatedAt = dto.CreatedAt;

        var createdAtUtc = DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc);
        var initialUptime = UptimeFormat.Format(time.GetUtcNow().UtcDateTime - createdAtUtc);

        _uptime = ticker
            .Select(_ => UptimeFormat.Format(time.GetUtcNow().UtcDateTime - createdAtUtc))
            .ToProperty(this, x => x.Uptime, initialUptime);

        _actionsEnabled = connected
            .CombineLatest(stopsInFlight, (isConnected, inFlight) => isConnected && !inFlight.Contains(Id))
            .ToProperty(this, x => x.ActionsEnabled, initialValue: false);

        // Mirrors TrayViewModel's tray-entry label shape (kind · vendor · repo leaf) — spec §7
        // doesn't pin the grid row's label text, so this stays consistent with the tray's for the
        // same agent rather than inventing a second format.
        var label = $"{dto.Kind} · {dto.Vendor} · {RepoLeaf}";
        StopCommand = ReactiveCommand.Create(() => actions.RequestStop(Id, label));
        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWeb(Id));
    }
}
