using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetrySpoolTests {
    static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-spool-{Guid.NewGuid():N}", "telemetry-spool.jsonl");

    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Drain_of_missing_file_is_empty() {
        var spool = new TelemetrySpool(NewPath());

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Appended_events_round_trip() {
        var spool = new TelemetrySpool(NewPath());
        spool.Append([Event("a"), Event("b")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(2);
        await Assert.That(drained[0].Name).IsEqualTo("a");
        await Assert.That(drained[1].Name).IsEqualTo("b");
        await Assert.That(drained[0].Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
    }

    [Test]
    public async Task Appends_accumulate_across_instances() {
        var path = NewPath();
        new TelemetrySpool(path).Append([Event("a")]);
        new TelemetrySpool(path).Append([Event("b")]);

        await Assert.That(new TelemetrySpool(path).DrainAll().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_empties_the_spool() {
        var path  = NewPath();
        var spool = new TelemetrySpool(path);
        spool.Append([Event("a")]);
        spool.Clear();

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_lines_are_skipped_not_fatal() {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json\n");
        var spool = new TelemetrySpool(path);
        spool.Append([Event("good")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(1);
        await Assert.That(drained[0].Name).IsEqualTo("good");
    }

    [Test]
    public async Task Type_mismatched_fields_are_skipped_not_fatal() {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        var path  = NewPath();
        var spool = new TelemetrySpool(path, maxEvents: 10);

        for (var i = 0; i < 25; i++) spool.Append([Event($"e{i}")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(10);
        await Assert.That(drained[0].Name).IsEqualTo("e15");
        await Assert.That(drained[^1].Name).IsEqualTo("e24");
    }
}
