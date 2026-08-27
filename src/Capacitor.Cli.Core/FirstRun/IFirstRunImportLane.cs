namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// The Import step's two halves, as a seam the poll loop can drive.
///
/// <para>Both live behind an interface for the same reason the machine-action lane does: they belong
/// to the import pipeline, which is in the CLI assembly, while the loop that sequences them is here.
/// It also means the loop's ordering and guards are testable without a disk scan or an upload.</para>
/// </summary>
public interface IFirstRunImportLane {
    /// <summary>
    /// Scans this machine for importable history, restricted to <paramref name="vendors"/>.
    ///
    /// <para><b>The filter is applied to the sources scanned, not to the counts afterwards</b>, so
    /// every figure reported is already scoped and the server does no vendor arithmetic. Null means no
    /// filter — an older answer this build could not read, which must not narrow to nothing.</para>
    ///
    /// <para>Null return means the scan could not produce a report. The screen keeps waiting, which is
    /// honest: nothing was learned.</para>
    /// </summary>
    /// <param name="asOf">The instant the reported windows resolve against. Handed in so the import
    /// that acts on these counts can be given the same one.</param>
    Task<ReportFirstRunImportRequest?> DiscoverAsync(
        IReadOnlyList<string>? vendors, DateTimeOffset asOf, CancellationToken ct);

    /// <summary>
    /// Runs the decision: one pass per level, because <c>--private</c> is per invocation.
    ///
    /// <para><b>Writes to the console.</b> The caller stops its own progress output first — two live
    /// Spectre renderables cannot share a terminal.</para>
    /// </summary>
    /// <param name="today">The date the window's <c>--since</c> resolves against. <b>The date the
    /// report's counts were built from</b>, not today's — a user who reads the screen across UTC
    /// midnight would otherwise be shown a figure for one boundary and given an import against the
    /// next, silently missing the day between.</param>
    /// <returns>
    /// What the passes moved, or <b>null when a pass produced no accounting at all</b> — it threw, or
    /// finished without reaching its own summary.
    ///
    /// <para>Null is not <c>(0,0,0)</c>, and the difference is the point: the sessions that pass was
    /// uploading are unaccounted, and there is no way to say "some unknown number failed" in three
    /// counts. Reporting the surviving pass's figures alone would state a clean import over a run that
    /// lost one, so the caller sends nothing and the screen keeps saying it cannot tell.</para>
    /// </returns>
    Task<FirstRunImportTotals?> ImportAsync(FirstRunImportAnswer answer, DateOnly today, CancellationToken ct);
}
