using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers <see cref="DshImportSource"/> discovery from the flat
/// <c>~/.cache/kcap/dsh/{id}.jsonl</c> layout the kcap Cordis plugin writes (cwd read from
/// the plugin's <c>{$kcap:"header", ...}</c> line) and the import-relevance line filter that
/// keeps the watermark in sync with the server's DeepSeekHarnessTranscriptNormalizer.
/// </summary>
public class DshImportSourceTests {
    // The plugin's real header line: {$kcap:"header", ...session.header}. The PoC omits
    // type:"session"; a real dsh session includes it. Either must be recognized.
    const string Header   = """{"$kcap":"header","version":0,"id":"sess-abc","createdAt":1785730000000,"cwd":"/work"}""";
    const string UserLine = """{"type":"user/message","seq":2,"time":1785730000100,"data":{"id":"u1","content":[{"type":"text","text":"hi"}]}}""";

    [Test]
    public async Task discovery_reads_flat_jsonl_and_header_cwd() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(tmp.Path, "sess-abc.jsonl"), Header + "\n" + UserLine + "\n");

        var src = new DshImportSource(sessionsDirOverride: tmp.Path);
        await Assert.That(src.IsAvailable).IsTrue();

        var found = await src.DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        var s = found[0];
        // RAW id (dashes kept): dsh ids are non-GUID, so the server leaves them dashed; the
        // CLI must send the SAME raw id for transcript + lifecycle or they split into two streams.
        await Assert.That(s.SessionId).IsEqualTo("sess-abc");
        await Assert.That(s.Vendor).IsEqualTo("dsh");
        await Assert.That(s.Cwd).IsEqualTo("/work");               // read from the $kcap header
        await Assert.That(s.SourceMeta!["DashedSessionId"]).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task discovery_canonicalizes_a_session_guid_id_to_the_36_char_contract() {
        using var tmp = new TempDir();
        // Real dsh id shape: session-<guid> (44 chars). The file keeps the raw name; the
        // discovered SessionId must be the embedded dashless GUID (<=36) so it lists.
        const string rawId = "session-e1d79e8a-9b62-4b23-b576-7e7493c09dba";
        await File.WriteAllTextAsync(Path.Combine(tmp.Path, rawId + ".jsonl"), Header + "\n" + UserLine + "\n");

        var found = await new DshImportSource(sessionsDirOverride: tmp.Path)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo("e1d79e8a9b624b23b5767e7493c09dba");
        await Assert.That(found[0].SessionId.Length).IsLessThanOrEqualTo(36);
    }

    [Test]
    public async Task discovery_surfaces_subagent_parent_from_header() {
        using var tmp = new TempDir();
        const string childHeader = """{"$kcap":"header","version":0,"id":"session-child","cwd":"/work","parentSession":"session-parent-abc","origin":"subagent"}""";
        await File.WriteAllTextAsync(Path.Combine(tmp.Path, "session-child.jsonl"), childHeader + "\n" + UserLine + "\n");

        var found = await new DshImportSource(sessionsDirOverride: tmp.Path)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SourceMeta!["ParentSession"]).IsEqualTo("session-parent-abc");
    }

    [Test]
    public async Task discovery_session_filter_matches_dashless_id() {
        using var tmp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(tmp.Path, "sess-abc.jsonl"), Header + "\n" + UserLine + "\n");

        var src = new DshImportSource(sessionsDirOverride: tmp.Path);

        var match = await src.DiscoverAsync(new DiscoveryFilters(null, "sess-abc", null, 1), CancellationToken.None);
        await Assert.That(match.Count).IsEqualTo(1);

        var miss = await src.DiscoverAsync(new DiscoveryFilters(null, "nomatch", null, 1), CancellationToken.None);
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"user/message","data":{}}""", true)]
    [Arguments("""{"type":"assistant/message","data":{}}""", true)]
    [Arguments("""{"type":"tool/result","data":{}}""", true)]
    [Arguments("""{"type":"assistant/chunk","data":{}}""", false)]
    [Arguments("""{"$kcap":"header","id":"s"}""", false)]
    [Arguments("""{"$kcap":"disposed","id":"s"}""", false)]
    [Arguments("""not json""", false)]
    public async Task is_import_relevant_line(string line, bool expected) {
        await Assert.That(DshImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-dsh-import-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
