using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class TelemetrySpoolTests {
    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Drain_of_missing_file_is_empty() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var spoolPath);
        var spool = new TelemetrySpool(spoolPath);

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Appended_events_round_trip() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var spoolPath);
        var spool = new TelemetrySpool(spoolPath);
        spool.Append([Event("a"), Event("b")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(2);
        await Assert.That(drained[0].Name).IsEqualTo("a");
        await Assert.That(drained[1].Name).IsEqualTo("b");
        await Assert.That(drained[0].Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
    }

    [Test]
    public async Task Appends_accumulate_across_instances() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var path);
        new TelemetrySpool(path).Append([Event("a")]);
        new TelemetrySpool(path).Append([Event("b")]);

        await Assert.That(new TelemetrySpool(path).DrainAll().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_empties_the_spool() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var path);
        var spool = new TelemetrySpool(path);
        spool.Append([Event("a")]);
        spool.Clear();

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_lines_are_skipped_not_fatal() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var path);
        File.WriteAllText(path, "{ not json\n");
        var spool = new TelemetrySpool(path);
        spool.Append([Event("good")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(1);
        await Assert.That(drained[0].Name).IsEqualTo("good");
    }

    [Test]
    public async Task Type_mismatched_fields_are_skipped_not_fatal() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var path);
        // Structurally valid JSON with a wrong field type: GetValue<string>() throws
        // InvalidOperationException, not JsonException, so a narrow filter lets it escape.
        File.WriteAllText(path, "{\"event\":123,\"timestamp\":\"1970-01-01T00:00:00+00:00\",\"properties\":{}}\n");
        var spool = new TelemetrySpool(path);
        spool.Append([Event("good")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(1);
        await Assert.That(drained[0].Name).IsEqualTo("good");
    }

    // Drop-oldest keeps the newest events, which are the ones most likely to still matter.
    [Test]
    public async Task Oldest_events_are_dropped_past_the_cap() {
        using var tmp = TempDir.WithPathTo("telemetry-spool.jsonl", out var path);
        var spool = new TelemetrySpool(path, maxEvents: 10);

        for (var i = 0; i < 25; i++) spool.Append([Event($"e{i}")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(10);
        await Assert.That(drained[0].Name).IsEqualTo("e15");
        await Assert.That(drained[^1].Name).IsEqualTo("e24");
    }

    [Test]
    public async Task Unusable_path_degrades_rather_than_throwing_on_append() {
        // Enumerated filters have missed exception categories twice in this namespace; the
        // never-throw constraint is absolute. Path.GetDirectoryName on a path with embedded
        // NUL returns "", and Directory.CreateDirectory("") throws ArgumentException, which
        // escaped the old enumerated filter. The widened catch in Append swallows it.
        var spool = new TelemetrySpool("bad\0path.jsonl");

        // Append must degrade, not throw — this exercises the widened catch
        spool.Append([Event("a")]);

        // DrainAll and Clear don't actually exercise their catches on this path
        // (File.Exists swallows the ArgumentException internally and returns false),
        // but calling them proves they don't throw on a junk path.
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
        spool.Clear();
    }
}
