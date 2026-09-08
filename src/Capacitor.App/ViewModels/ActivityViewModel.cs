using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One row of the decision log, projected for display. Time is a short local clock for the
/// table; TimeTip carries the full local stamp for hover. An unparseable decided_at renders
/// verbatim in both rather than throwing. IsAllowed drives the outcome color.
public sealed record ActivityRow(
    string Time, string TimeTip, string Outcome, bool IsAllowed, string Requester, string KindLabel,
    string RepoLeaf, string RepoFull, string Vendor, string SourceLabel);

/// Renders the consent decision log as the Activity tab (spec §7): pure file I/O via the injected
/// `read`, so the feed works with the daemon stopped or unreachable. No FileSystemWatcher —
/// refreshed on tab visibility, a 2-tick stat poll while visible, and an own-resolution nudge (App
/// wires RequestRefresh into ConsentPromptViewModel's onConcluded callback).
///
/// Constructed once at the composition root and lives for the app's lifetime, like ConsentService
/// — it subscribes to the SHARED ticker directly in the constructor rather than through
/// WhenActivated/window activation, since the prompt-window factory must capture this same
/// instance independently of MainWindowViewModel's own construction. The ticker already delivers
/// on the UI thread (UiTicker's doc comment), so no ObserveOn is needed here.
///
/// That constructor-scoped subscription is why this is IDisposable: the shared ticker is
/// Publish().RefCount(), so its Interval only tears down when the LAST subscriber goes — a
/// subscriber nobody disposes keeps a 1 Hz timer (and this object) alive past teardown, including
/// the startup-failure path where the app lingers on an error window. App disposes it in reverse
/// creation order with the other UI services.
public sealed class ActivityViewModel : ReactiveObject, IDisposable {
    readonly Func<ConsentLogReadResult> _read;
    readonly Func<string> _statKey;
    readonly IDisposable _tickSub;

    readonly ObservableCollectionExtended<ActivityRow> _rowsSource = new();
    public ReadOnlyObservableCollection<ActivityRow> Rows { get; }

    bool _isEmpty = true;
    public bool IsEmpty {
        get => _isEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    bool _visible;
    int _tickCount;
    string? _lastStatKey;
    bool _refreshInFlight;
    bool _disposed;

    enum RefreshMode { Gated, PrimeAndRead, ReadOnly }

    /// Test-only seam: the in-flight off-UI-thread stat+read hop this VM is currently awaiting, so
    /// a test can await the exact completion the single-flight guard below watches instead of a
    /// fixed delay. Null when idle.
    internal Task? PendingRefreshForTesting { get; private set; }

    public ActivityViewModel(Func<ConsentLogReadResult> read, Func<string> statKey, ITicker ticker) {
        _read = read;
        _statKey = statKey;
        Rows = new ReadOnlyObservableCollection<ActivityRow>(_rowsSource);

        _tickSub = ticker.Ticks.Subscribe(_ => OnTick());
    }

    public void Dispose() {
        _disposed = true;
        _tickSub.Dispose();
    }

    /// Tab-visibility trigger (spec §7): a true transition (tab selected AND window visible, per
    /// MainWindow.axaml.cs) does an immediate read and primes the poll's stat baseline so the very
    /// next tick doesn't immediately re-read the same unchanged content. A no-op on a repeated call
    /// with the same value.
    public void OnTabVisibleChanged(bool visible) {
        if (visible == _visible) return;
        _visible = visible;
        _tickCount = 0;
        if (!visible) return;

        TriggerRefresh(RefreshMode.PrimeAndRead);
    }

    /// Own-resolution nudge (spec §7): an immediate read now — the daemon appends the log record
    /// AFTER completing the resolve (RunContinuationsAsynchronously), so this ack-triggered read
    /// can beat the append. Eventual consistency relies on the next stat-poll tick, not on this
    /// call firing a second time — including when the single-flight guard drops this call because
    /// another refresh is already in flight.
    public void RequestRefresh() => TriggerRefresh(RefreshMode.ReadOnly);

    void OnTick() {
        if (!_visible) return;
        if (++_tickCount < 2) return;
        _tickCount = 0;
        TriggerRefresh(RefreshMode.Gated);
    }

    /// Single-flight: a trigger arriving while a stat+read is already running is dropped, never
    /// queued — latest-wins isn't needed because the next tick/refresh re-checks on its own. Both
    /// the stat call and the log read are blocking file I/O, so they run off the UI thread
    /// (Task.Run); only the resulting state mutation and Apply (Rows is a bound collection) run
    /// back on the UI thread, via an explicit Dispatcher hop rather than relying on an ambient
    /// SynchronizationContext capture.
    void TriggerRefresh(RefreshMode mode) {
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        PendingRefreshForTesting = RunRefreshAsync(mode);
    }

    async Task RunRefreshAsync(RefreshMode mode) {
        try {
            var previousKey = _lastStatKey;
            var (nextKey, result) = await Task.Run(() => ComputeOffUiThread(mode, previousKey)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyOutcome(nextKey, result));
        } finally {
            _refreshInFlight = false; // runs on the UI thread (InvokeAsync's continuation), but a plain bool write is safe either way
        }
    }

    // Gated (OnTick): re-reads only when the stat key changed since the previous check — the key
    // still advances even if the read that follows throws, same as before this moved off-thread.
    // PrimeAndRead (OnTabVisibleChanged true) always reads and always primes the baseline.
    // ReadOnly (RequestRefresh) never touches the stat key at all.
    (string? key, ConsentLogReadResult? result) ComputeOffUiThread(RefreshMode mode, string? previousKey) {
        if (mode == RefreshMode.ReadOnly) return (null, SafeRead());

        string key;
        try { key = _statKey(); } catch { key = "absent"; }
        if (mode == RefreshMode.Gated && key == previousKey) return (null, null);
        return (key, SafeRead());
    }

    ConsentLogReadResult? SafeRead() {
        try { return _read(); } catch { return null; } // swallowed — last-good rows stay on display
    }

    void ApplyOutcome(string? key, ConsentLogReadResult? result) {
        if (_disposed) return;
        if (key is not null) _lastStatKey = key;
        if (result is not null) Apply(result);
    }

    /// Display rule keyed off Complete (spec §7): a Complete read replaces the rows, including
    /// replacing them with the empty state when it is genuinely empty. An incomplete read never
    /// replaces existing rows — unless there are none yet, where the partial records are shown
    /// best-effort rather than leaving the feed with nothing at all.
    void Apply(ConsentLogReadResult result) {
        if (!result.Complete && _rowsSource.Count > 0) return;

        _rowsSource.Load(result.Records.Select(ToRow));
        IsEmpty = _rowsSource.Count == 0;
    }

    static ActivityRow ToRow(ConsentDecisionRecord r) {
        var (time, tip) = FormatTime(r.DecidedAt);
        return new(
            time, tip, r.Outcome, r.Outcome == "allowed", RequesterOf(r),
            ConsentPromptViewModel.KindLabelOf(r.Kind), RepoLabel.Leaf(r.RepoPath), r.RepoPath, r.Vendor,
            SourceLabelOf(r.Source));
    }

    static string RequesterOf(ConsentDecisionRecord r) =>
        !string.IsNullOrWhiteSpace(r.RequesterDisplay) ? r.RequesterDisplay
        : !string.IsNullOrWhiteSpace(r.Requester)      ? r.Requester
        : "unknown";

    // RoundtripKind, matching ConsentService.DeadlineFor: both parse the same daemon-written ISO
    // stamps, and one parse style across the app is one behavior to reason about.
    static (string Display, string Tip) FormatTime(string decidedAt) {
        if (!DateTimeOffset.TryParse(decidedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return (decidedAt, decidedAt);
        var local = parsed.ToLocalTime();
        return (
            local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture),
            local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }
    internal static string SourceLabelOf(string source) => source switch {
        "owner"          => "owner",
        "default"        => "default policy",
        "prompt_user"    => "you",
        "prompt_timeout" => "timeout",
        "prompt_no_ui"   => "no UI attached",
        _ => source.StartsWith("rule[", StringComparison.Ordinal) && source.EndsWith(']') ? "rule" : source,
    };
}
