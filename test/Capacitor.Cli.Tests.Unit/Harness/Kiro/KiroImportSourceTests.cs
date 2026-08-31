using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// Covers <see cref="KiroImportSource"/> discovery from <c>~/.kiro/sessions/cli</c>
/// (the <c>{id}.jsonl</c> + sibling <c>{id}.json</c> pair) and the import-relevance
/// line filter that keeps the watermark in sync with the server normalizer.
/// </summary>
public class KiroImportSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Dashed = "5f6c2b1a-9e3d-4a7c-bb12-1c2d3e4f5a6b";

    [Test]
    public async Task discovery_reads_jsonl_and_sibling_json_metadata() {
        using var tmp = new TempDir();

        tmp.CreateFile($"{Dashed}.jsonl",
            """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"hi"}]}}""" + "\n");
        tmp.CreateFile($"{Dashed}.json",
            """{"cwd":"/work","title":"Hi there","created_at":"2026-06-17T10:30:00Z","session_state":{"rts_model_state":{"model_info":{"model_id":"auto"}}}}""");

        var src = new KiroImportSource(Config.Root, tmp.Path);
        await Assert.That(src.IsAvailable).IsTrue();

        var found = await src.DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        var s = found[0];
        await Assert.That(s.SessionId).IsEqualTo(Dashed.Replace("-", ""));
        await Assert.That(s.Vendor).IsEqualTo(HarnessId.Kiro);
        await Assert.That(s.Cwd).IsEqualTo("/work");
        await Assert.That(s.SourceMeta!["DashedSessionId"]).IsEqualTo(Dashed);
        await Assert.That(s.SourceMeta!["Model"]).IsEqualTo("auto");
        await Assert.That(s.SourceMeta!["Title"]).IsEqualTo("Hi there");
    }

    [Test]
    public async Task discovery_session_filter_matches_dashless_id() {
        using var tmp = new TempDir();
        tmp.CreateFile($"{Dashed}.jsonl", "{}\n");

        var src = new KiroImportSource(Config.Root, tmp.Path);

        var match = await src.DiscoverAsync(new DiscoveryFilters(null, Dashed.Replace("-", ""), null, 1), CancellationToken.None);
        await Assert.That(match.Count).IsEqualTo(1);

        var miss = await src.DiscoverAsync(new DiscoveryFilters(null, "ffffffffffffffffffffffffffffffff", null, 1), CancellationToken.None);
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"version":"v1","kind":"Prompt","data":{}}""", true)]
    [Arguments("""{"version":"v1","kind":"AssistantMessage","data":{}}""", true)]
    [Arguments("""{"version":"v1","kind":"ToolResults","data":{}}""", true)]
    [Arguments("""{"version":"v1","kind":"Thinking","data":{}}""", false)]
    [Arguments("""not json""", false)]
    public async Task is_import_relevant_line(string line, bool expected) {
        await Assert.That(KiroImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }
}
