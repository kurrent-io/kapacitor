using Capacitor.Cli.Commands;
using Spectre.Console;

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

    [Test, NotInParallel]
    public async Task done_grid_leads_with_imported_skipped_and_failed() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            new(Loaded: 140, Resumed: 8, AlreadyLoaded: 3, TooShort: 1, Excluded: 0,
                ProbeError: 1, Errored: 1,
                TitlesGenerated: 0, TitlesSkipped: 0, TitlesFailed: 0,
                SummariesGenerated: 0, SummariesFailed: 0,
                RanBackground: false, RequestedSummaries: false),
            bySource: null));

        // 140+8 imported, 3+1 skipped, 1+1 failed: a resume counts as imported, a probe error as failed.
        await Assert.That(output).Contains("148 imported · 4 skipped · 2 failed");
    }

    [Test, NotInParallel]
    public async Task a_failure_says_what_did_not_land_without_calling_the_run_broken() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 10) with { Errored = 2 },
            bySource: null));

        await Assert.That(output).Contains("2 didn't land");
        // The run exits 0, so a bare count must not be left reading as a broken import: the remedy is
        // named, and it is true because import is idempotent.
        await Assert.That(output).Contains("Re-run to retry");
    }

    /// <summary>
    /// Skipped is not imported. Too-short and excluded sessions were deliberately never sent, so a
    /// failure note may not sweep them into a claim that everything else landed.
    /// </summary>
    [Test, NotInParallel]
    public async Task a_mixed_run_does_not_claim_the_skipped_sessions_landed() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 10) with { Excluded = 1, TooShort = 1, Errored = 1 },
            bySource: null));

        await Assert.That(output).Contains("10 imported · 2 skipped · 1 failed");
        await Assert.That(output).Contains("1 didn't land");
        await Assert.That(output).DoesNotContain("Everything else is in");
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

    /// <summary>
    /// The plain-text rows nest by the same step as the headings above them, so a redirected run of a
    /// nested import puts its counts under its section rather than level with it.
    /// </summary>
    [Test, NotInParallel]
    [Arguments(false, "  New")]
    [Arguments(true, "    New")]
    public async Task plain_rows_nest_with_their_heading(bool nested, string expected) {
        using var capture = ConsoleOutput.StartCapture("\n");

        new ImportCommand.ImportDisplay { Tty = false, Nested = nested }.WritePlanGrid(
            new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: null);

        var row = capture.GetCapturedOutput()
                         .Split('\n')
                         .First(l => l.TrimStart().StartsWith("New", StringComparison.Ordinal));

        await Assert.That(row).StartsWith(expected);
    }

    /// <summary>
    /// Column 0 belongs to headings, so a line the run writes for itself is indented under the one it
    /// belongs to — and one step deeper again where the whole run is nested inside a setup step.
    /// </summary>
    [Test, NotInParallel]
    [Arguments(false, "  Found 3 sessions.")]
    [Arguments(true, "    Found 3 sessions.")]
    public async Task a_line_sits_under_its_heading_rather_than_against_the_margin(
            bool nested, string expected) {
        using var capture = ConsoleOutput.StartCapture("\n");

        new ImportCommand.ImportDisplay { Tty = false, Nested = nested }.Line("Found 3 sessions.");

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(expected + "\n");
    }

    /// <summary>
    /// A grid ignores leading spaces, so padding is the only thing that can put the counts under the
    /// heading they belong to rather than against the margin the prose beside them is indented from.
    /// </summary>
    [Test, NotInParallel]
    public async Task the_counts_line_up_with_the_prose_around_them() {
        var originalConsole = AnsiConsole.Console;
        var buffer          = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(buffer),
        });

        // Pinned, because a rule fills the width and a heading wraps against it: a runner's console
        // reports its own, so an unpinned layout asserts whatever the host happens to be.
        AnsiConsole.Profile.Width = 120;

        try {
            new ImportCommand.ImportDisplay { Tty = true }.WritePlanGrid(
                new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
                bySource: null);
        } finally {
            AnsiConsole.Console = originalConsole;
        }

        var counts = buffer.ToString()
                           .Split('\n')
                           .First(l => l.Contains("New", StringComparison.Ordinal));

        await Assert.That(counts).StartsWith("  ");
    }

    /// <summary>
    /// A section heading is the structure of the output only where the run is itself the command. Nested
    /// inside a setup step it is subordinate to a rule already drawn, and is marked as such.
    /// </summary>
    [Test, NotInParallel]
    [Arguments(false, "== Discovering ==")]
    [Arguments(true, "  -- Discovering --")]
    public async Task a_phase_heading_is_subordinate_only_when_the_run_is_nested(
            bool nested, string expected) {
        using var capture = ConsoleOutput.StartCapture();

        new ImportCommand.ImportDisplay { Tty = false, Nested = nested }.BeginPhase("Discovering");

        await Assert.That(capture.GetCapturedOutput().Replace("\r\n", "\n")).Contains(expected);
    }

    /// <summary>
    /// The TTY half, which is the only place a rule exists at all — so a nesting regression that drew one
    /// would be invisible to the plain-text pins above.
    ///
    /// <para>Asserted structurally rather than by styling: the rule's glyph row is what reads as a step
    /// boundary, and no capture in this suite can see dim.</para>
    /// </summary>
    [Test, NotInParallel]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task on_a_terminal_only_an_unnested_section_draws_a_rule(bool nested, bool ruled) {
        var originalConsole = AnsiConsole.Console;
        var buffer          = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(buffer),
        });

        // Pinned, because a rule fills the width and a heading wraps against it: a runner's console
        // reports its own, so an unpinned layout asserts whatever the host happens to be.
        AnsiConsole.Profile.Width = 120;

        try {
            new ImportCommand.ImportDisplay { Tty = true, Nested = nested }.BeginPhase("Discovering");
        } finally {
            AnsiConsole.Console = originalConsole;
        }

        var output = buffer.ToString();

        await Assert.That(output).Contains("Discovering");
        // Spectre draws a rule as a run of box-drawing glyphs either side of the title.
        await Assert.That(output.Contains('─')).IsEqualTo(ruled);
        await Assert.That(output.Contains("  Discovering")).IsEqualTo(!ruled);
    }

    /// <summary>Quiet outranks both: <c>--discover --json</c>'s whole stdout has to parse.</summary>
    [Test, NotInParallel]
    public async Task a_quiet_run_draws_no_phase_heading_nested_or_not() {
        using var capture = ConsoleOutput.StartCapture();

        new ImportCommand.ImportDisplay { Tty = false, Quiet = true, Nested = true }.BeginPhase("Discovering");
        new ImportCommand.ImportDisplay { Tty = false, Quiet = true }.BeginPhase("Discovering");

        await Assert.That(capture.GetCapturedOutput()).DoesNotContain("Discovering");
    }

    static string CaptureNonTtyOutput(Action<ImportCommand.ImportDisplay> render) {
        using var capture = ConsoleOutput.StartCapture();
        render(new() { Tty = false });
        return capture.GetCapturedOutput();
    }
}
