namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Converts codex app-server cumulative token-usage snapshots (<c>thread/tokenUsage/updated.total</c>)
/// into per-event DELTAS. The canonical usage pipeline SUMS per-event usage, so feeding it cumulative
/// totals would double-count; and attributing a thread-cumulative total to the latest model would
/// mis-attribute everything before a <c>model/rerouted</c>. So the daemon deltas here and the caller
/// attributes each delta to the model resolved at that instant.
///
/// <para>Not thread-safe: driven from the single notification-handling path.</para>
/// </summary>
internal sealed class CodexUsageDeltaConverter {
    CodexTokenUsage? _previous;
    bool _baselineOnNext; // resume fallback: consume the next snapshot as baseline, emit nothing

    /// <summary>The per-event delta for a new cumulative snapshot, or <see langword="null"/> when the
    /// snapshot was consumed as a resume baseline and nothing should be emitted.</summary>
    public CodexTokenUsage? Convert(CodexTokenUsage total) {
        if (_baselineOnNext) {
            // A resumed round with no exact baseline: this snapshot is the baseline, emit nothing. Exact
            // when it precedes the round's first request; otherwise a bounded, fallback-only undercount.
            _baselineOnNext = false;
            _previous       = total;
            return null;
        }

        var previous = _previous;
        _previous = total;

        if (previous is not { } prev)
            return total; // first snapshot: the baseline was zero, so the whole total is the first delta

        if (IsReset(prev, total))
            return total; // a lower/reset cumulative total is a fresh baseline — contribute total_now

        return Delta(prev, total);
    }

    /// <summary>Sets an exact post-resume baseline (from <c>thread/read</c>) so conversion stays exact —
    /// no loss, no double-count across a resume.</summary>
    public void SetExactBaseline(CodexTokenUsage baseline) {
        _previous       = baseline;
        _baselineOnNext = false;
    }

    /// <summary>Fallback when no exact baseline is available: the next snapshot becomes the baseline and
    /// emits nothing.</summary>
    public void BaselineOnNextNotification() => _baselineOnNext = true;

    // A cumulative counter that dropped in any component means the thread reset/rebaselined; the caller
    // then contributes the whole new total, so Delta below is only ever reached on non-decreasing input
    // and never produces a negative component.
    static bool IsReset(CodexTokenUsage prev, CodexTokenUsage now) =>
        now.InputTokens           < prev.InputTokens
     || now.CachedInputTokens     < prev.CachedInputTokens
     || now.OutputTokens          < prev.OutputTokens
     || now.ReasoningOutputTokens < prev.ReasoningOutputTokens
     || now.TotalTokens           < prev.TotalTokens;

    static CodexTokenUsage Delta(CodexTokenUsage prev, CodexTokenUsage now) => new(
        InputTokens:           now.InputTokens           - prev.InputTokens,
        CachedInputTokens:     now.CachedInputTokens     - prev.CachedInputTokens,
        OutputTokens:          now.OutputTokens          - prev.OutputTokens,
        ReasoningOutputTokens: now.ReasoningOutputTokens - prev.ReasoningOutputTokens,
        TotalTokens:           now.TotalTokens           - prev.TotalTokens);
}
