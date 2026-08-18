using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers <see cref="DshImportSource"/> discovery from the per-session
/// <c>&lt;sessions&gt;/&lt;id&gt;/session.jsonl</c> layout (cwd read from the
/// <c>{type:"session"}</c> header line) and the import-relevance line filter that keeps
/// the watermark in sync with the server's DeepSeekHarnessTranscriptNormalizer.
/// </summary>
public class DshImportSourceTests {
    const string Header = """{"type":"session","version":0,"id":"sess-abc","createdAt":1785730000000,"cwd":"/work"}""";
    const string UserLine = """{"type":"user/message","seq":2,"time":1785730000100,"data":{"id":"u1","content":[{"type":"text","text":"hi"}]}}""";

    [Test]
    public async Task discovery_reads_per_session_jsonl_and_header_cwd() {
        using var tmp = new TempDir();

        var sessionDir = Path.Combine(tmp.Path, "sess-abc");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.jsonl"), Header + "\n" + UserLine + "\n");

        var src = new DshImportSource(sessionsDirOverride: tmp.Path);
        await Assert.That(src.IsAvailable).IsTrue();

        var found = await src.DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        var s = found[0];
        await Assert.That(s.SessionId).IsEqualTo("sessabc");       // dashless canonical id
        await Assert.That(s.Vendor).IsEqualTo("dsh");
        await Assert.That(s.Cwd).IsEqualTo("/work");
        await Assert.That(s.SourceMeta!["DashedSessionId"]).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task discovery_session_filter_matches_dashless_id() {
        using var tmp = new TempDir();
        var sessionDir = Path.Combine(tmp.Path, "sess-abc");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.jsonl"), Header + "\n" + UserLine + "\n");

        var src = new DshImportSource(sessionsDirOverride: tmp.Path);

        var match = await src.DiscoverAsync(new DiscoveryFilters(null, "sessabc", null, 1), CancellationToken.None);
        await Assert.That(match.Count).IsEqualTo(1);

        var miss = await src.DiscoverAsync(new DiscoveryFilters(null, "nomatch", null, 1), CancellationToken.None);
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"user/message","data":{}}""", true)]
    [Arguments("""{"type":"assistant/message","data":{}}""", true)]
    [Arguments("""{"type":"tool/result","data":{}}""", true)]
    [Arguments("""{"type":"assistant/chunk","data":{}}""", false)]
    [Arguments("""{"type":"session","id":"s"}""", false)]
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
