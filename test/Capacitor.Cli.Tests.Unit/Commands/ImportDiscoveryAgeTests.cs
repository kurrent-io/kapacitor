using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A window's count is only worth showing next to a choice if it predicts the import that choice
/// makes — so the age each vendor is bucketed by has to be the one <c>--since</c> compares.
/// </summary>
/// <remarks>
/// There is no single rule to reuse: Codex prunes on the rollout's day directory, Claude filters on
/// the transcript's first message timestamp falling back to the file's last write, and the rest carry
/// a <c>FirstTimestamp</c> from discovery. Using any one of those for all three over-counts the narrow
/// windows for exactly the long-running sessions people have most of.
/// </remarks>
public class ImportDiscoveryAgeTests {
    static DiscoveredSession Session(string vendor, DateTimeOffset? first, string? filePath = null, string pathKey = "FilePath") =>
        new(SessionId: "s1",
            Vendor: vendor,
            Cwd: null,
            FirstTimestamp: first,
            SourceMeta: filePath is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { [pathKey] = filePath });

    [Test]
    public async Task Codex_is_dated_by_the_day_directory_the_rollout_sits_in() {
        using var tmp = new TempDir();
        var       day = tmp.CreateDir("sessions", "2026", "01", "05");
        var       roll = day.CreateFile("rollout-abc.jsonl", "{}");

        var age = ImportDiscoveryAge.Of(Session("codex", null, roll));

        // Not the file's mtime, which is now: --since prunes Codex on the directory alone.
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(new DateTime(2026, 1, 5));
    }

    [Test]
    public async Task Claude_is_dated_by_the_transcripts_first_timestamp_not_its_mtime() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("session.jsonl",
            ["""{"type":"user","timestamp":"2026-01-05T10:00:00Z","message":{"content":"hi"}}""",
             """{"type":"user","timestamp":"2026-08-01T10:00:00Z","message":{"content":"later"}}"""]);

        var age = ImportDiscoveryAge.Of(Session("claude", null, path));

        // A session started in January and appended to today belongs to January, which is the window
        // --since would place it in. Taking mtime would count it inside a 30-day window it is not in.
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(new DateTime(2026, 1, 5));
    }

    [Test]
    public async Task Claude_falls_back_to_the_last_write_when_no_timestamp_can_be_read() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("garbage.jsonl", "not json at all");

        var age = ImportDiscoveryAge.Of(Session("claude", null, path));

        // Same fallback the --since filter takes when the metadata has no timestamp.
        await Assert.That(age).IsNotNull();
        await Assert.That(age!.Value.UtcDateTime.Date).IsEqualTo(DateTime.UtcNow.Date);
    }

    [Test]
    [Arguments("gemini")]
    [Arguments("kiro")]
    [Arguments("pi")]
    [Arguments("copilot")]
    [Arguments("antigravity")]
    [Arguments("opencode")]
    [Arguments("cursor")]
    public async Task Other_vendors_use_the_first_timestamp_discovery_already_resolved(string vendor) {
        var known = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        // These key their path as "TranscriptPath", so a FilePath-based fallback would never fire for
        // them anyway — the point is that it does not need to.
        var age = ImportDiscoveryAge.Of(Session(vendor, known, "/tmp/whatever.jsonl", pathKey: "TranscriptPath"));

        await Assert.That(age).IsEqualTo(known);
    }

    [Test]
    public async Task An_age_that_cannot_be_determined_is_null_rather_than_guessed() {
        await Assert.That(ImportDiscoveryAge.Of(Session("gemini", null))).IsNull();
    }

    [Test]
    [Arguments("sessions/2026/13/05/rollout-a.jsonl")]
    [Arguments("sessions/2026/02/30/rollout-a.jsonl")]
    [Arguments("sessions/notayear/01/05/rollout-a.jsonl")]
    public async Task A_codex_path_that_is_not_a_date_directory_is_not_read_as_one(string relative) {
        await Assert.That(ImportDiscoveryAge.DayFromCodexPath(relative)).IsNull();
    }
}
