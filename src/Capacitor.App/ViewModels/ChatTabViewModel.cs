using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable }

/// The Chat tab: the session's transcript, tailed and projected into chat rows, plus the composer
/// that sends through the sibling terminal. Ctor-scoped; TeardownAsync is the one exit.
///
/// Path identity is part of the read generation: a distinct transcript_path clears the rows and
/// installs a fresh tail in one UI-thread step, and any read still in flight for the old file
/// completes under a stale generation and is discarded.
public sealed class ChatTabViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly string _agentId;
    readonly TerminalTabViewModel _terminal;
    readonly ITranscriptProjection? _projection;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly CompositeDisposable _disposables = new();
    readonly AvaloniaList<ChatItemViewModel> _items = new();
    readonly Dictionary<string, ToolCallItem> _pendingTools = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _loggedFailures = new(StringComparer.Ordinal);

    /// The tail and the generation it belongs to, taken as one reference: reading the two
    /// separately lets a switch land between them and tag a read of the old file with the new
    /// generation, which Apply's guard would then wave through onto the freshly cleared list.
    sealed record TailLease(JsonlTail Tail, int Generation);

    int _generation;
    int _readInFlight;
    string? _path;
    volatile TailLease? _lease;
    ITimer? _timer;
    volatile Task? _pendingRead;

    public IAvaloniaReadOnlyList<ChatItemViewModel> Items => _items;

    ChatTabPhase _phase;
    public ChatTabPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
        }
    }

    public string PhaseNote => Phase switch {
        ChatTabPhase.Waiting     => "Waiting for the transcript…",
        ChatTabPhase.Missing     => "The transcript file is missing",
        ChatTabPhase.Unavailable => "No chat view for this harness",
        _                        => "",
    };

    /// Test-only seam: the read in flight, or the last one started. A switch that loses the
    /// in-flight CAS starts no read of its own, so this still points at the previous file's read —
    /// await that, advance one tick, then await again to see the new path's first rows.
    internal Task? PendingReadForTesting => _pendingRead;

    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, TerminalTabViewModel terminal,
            ITranscriptProjection? projection, IUrlOpener opener, TimeProvider time) {
        _agentId = agentId;
        _terminal = terminal;
        _projection = projection;
        _opener = opener;
        _time = time;
        _phase = projection is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged)
            .DisposeWith(_disposables);

        if (projection is not null)
            _timer = time.CreateTimer(_ => OnTick(), null, PollInterval, PollInterval);

        // Task 12: composer, footer and link members are constructed here.
    }

    void OnAgentsChanged(IChangeSet<AgentStatusDto, string> changes) {
        foreach (var change in changes) {
            if (change.Key != _agentId || change.Reason is not (ChangeReason.Add or ChangeReason.Update)) continue;
            OnDto(change.Current);
        }
    }

    void OnDto(AgentStatusDto dto) {
        if (_projection is not null && dto.TranscriptPath is { } path && path != _path) SwitchPath(path);
    }

    void SwitchPath(string path) {
        _items.Clear();
        _pendingTools.Clear();
        _path = path;
        _lease = new TailLease(new JsonlTail(path), Interlocked.Increment(ref _generation));
        Phase = ChatTabPhase.Waiting;
        // A switch re-announces the note even when it lands on the phase already showing: the rows
        // are gone, so the view has to re-read what stands in for them.
        this.RaisePropertyChanged(nameof(PhaseNote));
        OnTick();
    }

    void OnTick() {
        if (_lease is not { } lease || _projection is not { } projection) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(lease.Tail, projection, lease.Generation);
    }

    async Task ReadAndApplyAsync(JsonlTail tail, ITranscriptProjection projection, int generation) {
        try {
            var (read, envelopes) = await Task.Run(() => {
                var result = tail.ReadAppended();
                var list = new List<AcpEventEnvelope>();
                foreach (var line in result.Lines) {
                    try { list.AddRange(projection.Project(line)); }
                    catch (Exception ex) { LogOnce($"projection: {ex.Message}"); }
                }
                return (result, list);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => Apply(generation, read, envelopes));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }

    void Apply(int generation, TailRead read, List<AcpEventEnvelope> envelopes) {
        if (generation != Volatile.Read(ref _generation)) return;

        switch (read.Status) {
            case TailStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return;
            case TailStatus.Failed:
                LogOnce(read.Failure ?? "read failed");
                return;
            case TailStatus.Reset:
                _items.Clear();
                _pendingTools.Clear();
                break;
        }

        Phase = ChatTabPhase.Reading;
        if (envelopes.Count == 0) return;

        var fresh = new List<ChatItemViewModel>();
        foreach (var e in envelopes) {
            switch (e.Kind) {
                case AcpEventKind.UserMessage:
                    fresh.Add(new UserTurnItem(e.Text ?? ""));
                    break;
                case AcpEventKind.AssistantText:
                    fresh.Add(new AssistantTextItem(e.Text ?? ""));
                    break;
                case AcpEventKind.ToolCall: {
                    var item = new ToolCallItem(e.ToolName ?? "tool", ToolDetail.From(e.ToolInputJson));
                    if (e.ToolCallId is { } id) _pendingTools[id] = item;
                    fresh.Add(item);
                    break;
                }
                case AcpEventKind.ToolResult:
                    if (e.ToolCallId is { } resultId && _pendingTools.Remove(resultId, out var call))
                        call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                    break;
            }
        }
        if (fresh.Count > 0) _items.AddRange(fresh);
    }

    void LogOnce(string reason) {
        if (_loggedFailures.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}");
    }

    public Task TeardownAsync() {
        Interlocked.Increment(ref _generation);
        _lease = null;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        return Task.CompletedTask;
    }
}
