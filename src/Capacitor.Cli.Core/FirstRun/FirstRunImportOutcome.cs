namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Why an import moved nothing, as a coded token. Three zeroes are also what a clean run over an
/// already-loaded history looks like, so this is what separates a refusal from a no-op.
///
/// <para><b>The server's closed set, spelled once.</b> It rejects a token it does not know and rejects
/// any token at all on an outcome that moved something, so a second spelling here is a silent wire
/// break rather than a rejected field.</para>
/// </summary>
public static class FirstRunImportOutcomeReasons {
    /// <summary>Repositories were chosen and no vendor on this machine could be read for them. The one
    /// otherwise-silent failure: the counts alone would claim a clean import of history that never
    /// moved.</summary>
    public const string NoReadableAgents = "no_readable_agents";

    /// <summary>The decision named a window or a titles answer this build cannot map, so none of it was
    /// acted on. Reported rather than retried: a newer server's vocabulary does not become readable by
    /// polling again.</summary>
    public const string DecisionUnreadable = "decision_unreadable";

    /// <summary>A pass was lost, so the run's counts are unaccounted. Three zeroes rather than a partial
    /// tally: the passes that did survive would otherwise read as a clean import.</summary>
    public const string RunFailed = "run_failed";

    public static readonly IReadOnlyList<string> All = [NoReadableAgents, DecisionUnreadable, RunFailed];

    public static bool IsKnown(string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>
/// What one import run moved, summed across its passes.
///
/// <para><b>The three counts the flow's route takes</b>, and nothing derived: the screen's own figures
/// come from a read of the sessions themselves, so these say what the machine attempted rather than
/// what is searchable yet.</para>
/// </summary>
/// <param name="Imported">Uploaded — loaded or resumed, which is one outcome and not two.</param>
/// <param name="Skipped">Deliberately left: already here, too short, or an excluded repository. Not a
/// shortfall, and the copy downstream must not read as one.</param>
/// <param name="Failed">Should have landed and did not, so re-running retries exactly these.
/// <b>Includes a session held back because it could not be made private</b> — the visibility preflight
/// drops those before the upload, and they are otherwise in none of the three.</param>
public readonly record struct FirstRunImportTotals(int Imported, int Skipped, int Failed) {
    public static FirstRunImportTotals operator +(FirstRunImportTotals a, FirstRunImportTotals b) =>
        new(a.Imported + b.Imported, a.Skipped + b.Skipped, a.Failed + b.Failed);
}
