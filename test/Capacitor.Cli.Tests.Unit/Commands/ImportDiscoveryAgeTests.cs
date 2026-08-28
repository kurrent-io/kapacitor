using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A window's count is only worth showing next to a choice if it predicts the import that choice
/// makes — so the age each source is bucketed by has to be the one its own <c>--since</c> compares.
/// </summary>
/// <remarks>
/// There is no single rule to reuse: Codex prunes on the rollout's day directory, Claude filters on
/// the transcript's first message timestamp falling back to the file's last write, and the rest carry
/// a <c>FirstTimestamp</c> from discovery. Using any one of those for all three over-counts the narrow
/// windows for exactly the long-running sessions people have most of.
/// </remarks>
internal sealed class ImportDiscoveryAgeTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static DiscoveredSession Session(
            HarnessId vendor, DateTimeOffset? first, string? filePath = null, string pathKey = "FilePath") =>
        new(SessionId: "s1",
            Vendor: vendor,
            Cwd: null,
            FirstTimestamp: first,
            SourceMeta: filePath is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { [pathKey] = filePath });

    [Test]
    public async Task Codex_is_dated_by_the_day_directory_the_rollout_sits_in() {
        using var tmp  = new TempDir();
        var       day  = tmp.CreateDir("sessions", "2026", "01", "05");
        var       roll = day.CreateFile("rollout-abc.jsonl", "{}");

        var age = new CodexImportSource(Config.Root, CodexHarness.FromEnvironment(Home).Paths.Sessions).DiscoveryAge(Session(HarnessId.Codex, null, roll));

        // Not the file's mtime, which is now: --since prunes Codex on the directory alone.
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(new DateTime(2026, 1, 5));
    }

    [Test]
    public async Task Claude_is_dated_by_the_transcripts_first_timestamp_not_its_mtime() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("session.jsonl", [
            /*lang=json*/ "{\"type\":\"user\",\"timestamp\":\"2026-01-05T10:00:00Z\",\"message\":{\"content\":\"hi\"}}",
            /*lang=json*/ "{\"type\":\"user\",\"timestamp\":\"2026-08-01T10:00:00Z\",\"message\":{\"content\":\"later\"}}",
        ]);

        var age = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects).DiscoveryAge(Session(HarnessId.Claude, null, path));

        // A session started in January and appended to today belongs to January, which is the window
        // --since places it in. Taking mtime would count it inside a 30-day window it is not in.
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(new DateTime(2026, 1, 5));
    }

    [Test]
    public async Task Claude_keeps_scanning_past_a_malformed_line_and_beyond_the_first_few() {
        // The metadata extraction import uses scans 50 lines and skips unparseable ones. Stopping
        // sooner, or at the first bad record, silently substitutes the file's mtime for a timestamp
        // the import then finds — filing a months-old session inside a 30-day window.
        using var tmp   = new TempDir();
        var       lines = new List<string> { "{ this is not json" };

        lines.AddRange(Enumerable.Repeat("{\"type\":\"summary\"}", 20));
        lines.Add("{\"type\":\"user\",\"timestamp\":\"2026-01-05T10:00:00Z\",\"message\":{\"content\":\"hi\"}}");

        var path = tmp.CreateFile("session.jsonl", [.. lines]);

        var age = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects).DiscoveryAge(Session(HarnessId.Claude, null, path));

        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(new DateTime(2026, 1, 5));
    }

    [Test]
    public async Task Claude_falls_back_to_the_last_write_when_no_timestamp_can_be_read() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("garbage.jsonl", "not json at all");

        var age = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects).DiscoveryAge(Session(HarnessId.Claude, null, path));

        // Same fallback the --since filter takes when the metadata carries no timestamp.
        await Assert.That(age).IsNotNull();
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(DateTime.UtcNow.Date);
    }

    // Named rather than passed as the source itself: TUnit needs a public test method and
    // IImportSource is internal, so the parameter cannot be the interface.
    [Test]
    [Arguments("gemini")]
    [Arguments("kiro")]
    [Arguments("pi")]
    [Arguments("copilot")]
    [Arguments("antigravity")]
    [Arguments("opencode")]
    [Arguments("cursor")]
    public async Task Sources_that_resolve_a_first_timestamp_are_dated_by_it(string vendor) {
        var known  = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var source = SourceFor(vendor);

        // These key their path as "TranscriptPath", so a FilePath-based fallback could never fire for
        // them — the point is that it does not need to.
        var age = source.DiscoveryAge(
            Session(source.Vendor, known, "/tmp/whatever.jsonl", pathKey: "TranscriptPath"));

        await Assert.That(age).IsEqualTo(known);
    }

    /// <summary>Every source but Claude and Codex, which resolve no timestamp during discovery.</summary>
    IImportSource SourceFor(string vendor) => vendor switch {
        "gemini"      => new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir),
        "kiro"        => new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir),
        "pi"          => new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir),
        "copilot"     => new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths),
        "antigravity" => new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths),
        "opencode"    => new OpenCodeImportSource(
            Path.Combine(OpenCodeHarness.FromEnvironment(Home).Paths.DataDir, "opencode.db"),
            OpenCodeHarness.FromEnvironment(Home).Paths.ImportLedgerJson),
        "cursor"      => NewCursorSource(),
        _             => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, null),
    };

    CursorImportSource NewCursorSource() {
        var paths = CursorHarness.FromEnvironment(Home).Paths;

        return new(Config.Root, paths.ProjectsDir, paths.WorkspaceStorageDir);
    }

    [Test]
    public async Task An_age_that_cannot_be_determined_is_null_rather_than_guessed() {
        await Assert.That(SourceFor("gemini").DiscoveryAge(Session(HarnessId.Gemini, null))).IsNull();
    }

    [Test]
    [Arguments("sessions/2026/13/05/rollout-a.jsonl")]
    [Arguments("sessions/2026/02/30/rollout-a.jsonl")]
    [Arguments("sessions/notayear/01/05/rollout-a.jsonl")]
    public async Task A_codex_path_that_is_not_a_date_directory_is_not_read_as_one(string relative) {
        await Assert.That(CodexDiscoveryAge.DayFromPath(relative)).IsNull();
    }
}
