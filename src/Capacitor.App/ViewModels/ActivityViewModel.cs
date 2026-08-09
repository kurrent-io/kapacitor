using System.Collections.ObjectModel;
using System.Globalization;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One row of the decision log, projected for display (spec §7). Time is local
/// ("yyyy-MM-dd HH:mm:ss"); an unparseable decided_at renders verbatim rather than throwing.
/// IsAllowed drives the outcome badge color; Outcome carries the raw wire value.
public sealed record ActivityRow(
    string Time, string Outcome, bool IsAllowed, string Requester, string KindLabel,
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
public sealed class ActivityViewModel : ReactiveObject {
    readonly Func<ConsentLogReadResult> _read;
    readonly Func<string> _statKey;

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

    public ActivityViewModel(Func<ConsentLogReadResult> read, Func<string> statKey, ITicker ticker) {
        _read = read;
        _statKey = statKey;
        Rows = new ReadOnlyObservableCollection<ActivityRow>(_rowsSource);

        ticker.Ticks.Subscribe(_ => OnTick());
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

        try { _lastStatKey = _statKey(); } catch { _lastStatKey = "absent"; }
        SafeRefresh();
    }

    /// Own-resolution nudge (spec §7): an immediate read now — the daemon appends the log record
    /// AFTER completing the resolve (RunContinuationsAsynchronously), so this ack-triggered read
    /// can beat the append. Eventual consistency relies on the next stat-poll tick, not on this
    /// call firing a second time.
    public void RequestRefresh() => SafeRefresh();

    void OnTick() {
        if (!_visible) return;
        if (++_tickCount < 2) return;
        _tickCount = 0;

        string key;
        try { key = _statKey(); } catch { key = "absent"; }
        if (key == _lastStatKey) return;
        _lastStatKey = key;
        SafeRefresh();
    }

    void SafeRefresh() {
        ConsentLogReadResult result;
        try { result = _read(); } catch { return; } // swallowed — last-good rows stay on display
        Apply(result);
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

    static ActivityRow ToRow(ConsentDecisionRecord r) => new(
        FormatTime(r.DecidedAt), r.Outcome, r.Outcome == "allowed", RequesterOf(r),
        ConsentPromptViewModel.KindLabelOf(r.Kind), RepoLabel.Leaf(r.RepoPath), r.RepoPath, r.Vendor,
        SourceLabelOf(r.Source));

    static string RequesterOf(ConsentDecisionRecord r) =>
        !string.IsNullOrWhiteSpace(r.RequesterDisplay) ? r.RequesterDisplay
        : !string.IsNullOrWhiteSpace(r.Requester)      ? r.Requester
        : "unknown";

    static string FormatTime(string decidedAt) =>
        DateTimeOffset.TryParse(decidedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : decidedAt; // unparseable — rendered verbatim rather than thrown

    internal static string SourceLabelOf(string source) => source switch {
        "owner"          => "owner",
        "default"        => "default policy",
        "prompt_user"    => "you",
        "prompt_timeout" => "timeout",
        "prompt_no_ui"   => "no UI attached",
        _ => source.StartsWith("rule[", StringComparison.Ordinal) && source.EndsWith(']') ? "rule" : source,
    };
}
