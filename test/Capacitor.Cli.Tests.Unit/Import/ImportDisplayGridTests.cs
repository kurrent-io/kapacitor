using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Import;

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
            MakeFinal(loaded: 3, ranBackground: true, requestedSummaries: true, titlesGenerated: 3, summariesGenerated: 3),
            bySource: null));

        await Assert.That(output).Contains("Titles");
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

    static ImportCommand.FinalCounts MakeFinal(
            int  loaded,
            bool ranBackground      = false,
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
        RequestedSummaries: requestedSummaries
    );

    static string CaptureNonTtyOutput(Action<ImportCommand.ImportDisplay> render) {
        var sw      = new StringWriter();
        var prevOut = Console.Out;
        Console.SetOut(sw);
        try {
            render(new() { Tty = false });
        } finally {
            Console.SetOut(prevOut);
        }
        return sw.ToString();
    }
}
