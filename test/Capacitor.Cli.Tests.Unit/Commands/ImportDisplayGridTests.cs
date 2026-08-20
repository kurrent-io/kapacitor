using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportDisplayGridTests {
    [Test, NotInParallel]
    public async Task plan_grid_renders_no_by_source_when_single_source() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: new Dictionary<string, ImportCommand.ClassificationCounts> {
                ["claude"] = new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            }));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel]
    public async Task plan_grid_renders_by_source_section_when_multiple_sources() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new(New: 7, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: new Dictionary<string, ImportCommand.ClassificationCounts> {
                ["claude"] = new(New: 4, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
                ["codex"]  = new(New: 3, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            }));

        await Assert.That(output).Contains("By source");
        await Assert.That(output).Contains("claude");
        await Assert.That(output).Contains("codex");
    }

    [Test, NotInParallel]
    public async Task plan_grid_renders_no_by_source_when_breakdown_null() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: null));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel]
    public async Task done_grid_renders_no_by_source_when_single_source() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 5),
            bySource: new Dictionary<string, ImportCommand.FinalCounts> {
                ["claude"] = MakeFinal(loaded: 5),
            }));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel]
    public async Task done_grid_renders_by_source_section_when_multiple_sources() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 7),
            bySource: new Dictionary<string, ImportCommand.FinalCounts> {
                ["claude"] = MakeFinal(loaded: 4),
                ["codex"]  = MakeFinal(loaded: 3),
            }));

        await Assert.That(output).Contains("By source");
        await Assert.That(output).Contains("claude");
        await Assert.That(output).Contains("codex");
    }

    [Test, NotInParallel]
    public async Task done_grid_renders_no_by_source_when_breakdown_null() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 5),
            bySource: null));

        await Assert.That(output).DoesNotContain("By source");
    }

    // Titles/Summaries rows must appear iff background work ran (the inverted guard dropped them).
    [Test, NotInParallel]
    public async Task done_grid_renders_titles_and_summaries_rows_when_background_ran() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 3, ranBackground: true, requestedTitles: true, requestedSummaries: true,
                      titlesGenerated: 3, summariesGenerated: 3),
            bySource: null));

        await Assert.That(output).Contains("Titles");
        await Assert.That(output).Contains("Summaries");
    }

    [Test, NotInParallel]
    public async Task done_grid_omits_the_titles_row_when_titling_was_skipped() {
        // --skip-title --generate-summaries: background work ran, but no titling did. A row of zeroes
        // here would read as titling that found nothing.
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 3, ranBackground: true, requestedSummaries: true, summariesGenerated: 3),
            bySource: null));

        await Assert.That(output).DoesNotContain("Titles");
        await Assert.That(output).Contains("Summaries");
    }

    [Test, NotInParallel]
    public async Task done_grid_omits_titles_and_summaries_rows_when_no_background_work() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 3),
            bySource: null));

        await Assert.That(output).DoesNotContain("Titles");
        await Assert.That(output).DoesNotContain("Summaries");
    }

    /// <summary>
    /// The headline: three buckets, because import distinguishes three. Rounding them into one number
    /// would hide a failure the command already knows about.
    /// </summary>
    [Test, NotInParallel]
    public async Task done_grid_leads_with_imported_skipped_and_failed() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            new(Loaded: 140, Resumed: 8, AlreadyLoaded: 3, TooShort: 1, Excluded: 0,
                ProbeError: 1, Errored: 1,
                TitlesGenerated: 0, TitlesSkipped: 0, TitlesFailed: 0,
                SummariesGenerated: 0, SummariesFailed: 0,
                RanBackground: false, RequestedSummaries: false),
            bySource: null));

        // A resume is an import that finished; too-short and already-there are choices, not failures;
        // a probe error and an upload error are both "did not land".
        await Assert.That(output).Contains("148 imported · 4 skipped · 2 failed");
    }

    [Test, NotInParallel]
    public async Task a_failure_says_what_did_not_land_without_calling_the_run_broken() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 10) with { Errored = 2 },
            bySource: null));

        await Assert.That(output).Contains("2 didn't land");
        await Assert.That(output).Contains("re-run to retry");
        // The run itself succeeded — it exits 0 — so the note has to place the failure against what
        // did land, rather than leave a bare red count reading as a broken import.
        await Assert.That(output).Contains("Everything else is in");
    }

    [Test, NotInParallel]
    public async Task a_clean_run_says_nothing_about_retrying() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(MakeFinal(loaded: 10), bySource: null));

        await Assert.That(output).Contains("10 imported · 0 skipped · 0 failed");
        await Assert.That(output).DoesNotContain("re-run");
    }

    static ImportCommand.FinalCounts MakeFinal(
            int  loaded,
            bool ranBackground      = false,
            bool requestedTitles    = false,
            bool requestedSummaries = false,
            int  titlesGenerated    = 0,
            int  summariesGenerated = 0
        ) => new(
        Loaded: loaded,
        Resumed: 0,
        AlreadyLoaded: 0,
        TooShort: 0,
        Excluded: 0,
        ProbeError: 0,
        Errored: 0,
        TitlesGenerated: titlesGenerated,
        TitlesSkipped: 0,
        TitlesFailed: 0,
        SummariesGenerated: summariesGenerated,
        SummariesFailed: 0,
        RanBackground: ranBackground,
        RequestedSummaries: requestedSummaries,
        RequestedTitles: requestedTitles
    );

    static string CaptureNonTtyOutput(Action<ImportCommand.ImportDisplay> render) {
        using var capture = ConsoleOutput.StartCapture();
        render(new() { Tty = false });
        return capture.GetCapturedOutput();
    }
}
