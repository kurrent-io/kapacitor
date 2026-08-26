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
    Task<ReportFirstRunImportRequest?> DiscoverAsync(IReadOnlyList<string>? vendors, CancellationToken ct);

    /// <summary>
    /// Runs the decision: one pass per level, because <c>--private</c> is per invocation.
    ///
    /// <para><b>Writes to the console.</b> The caller stops its own progress output first — two live
    /// Spectre renderables cannot share a terminal.</para>
    /// </summary>
    Task ImportAsync(FirstRunImportAnswer answer, CancellationToken ct);
}
