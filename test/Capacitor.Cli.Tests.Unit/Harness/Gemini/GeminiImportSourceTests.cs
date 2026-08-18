using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Gemini;

namespace Capacitor.Cli.Tests.Unit.Harness.Gemini;

/// <summary>
/// discovery walks <c>~/.gemini/tmp/&lt;project&gt;/chats/session-*.jsonl</c>
/// and reads the FULL dashed session id from the file's header record (the
/// filename only carries the 8-char shortId). <see cref="GeminiImportSource.IsImportRelevantLine"/>
/// must mirror the server normalizer's skip rules so the import watermark
/// compares against the right line.
/// </summary>
public class GeminiImportSourceTests {
    static void WriteSession(string tmpDir, string project, string fileName, string sessionId, params string[] extraLines) {
        var chats = Path.Combine(tmpDir, project, "chats");
        Directory.CreateDirectory(chats);

        var header = $$"""{"sessionId":"{{sessionId}}","projectHash":"abc","startTime":"2026-06-17T14:10:03.447Z","kind":"main"}""";
        File.WriteAllLines(Path.Combine(chats, fileName), new[] { header }.Concat(extraLines));
    }

    [Test]
    public async Task discover_reads_full_session_id_from_header() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-2026-06-17T14-10-11111111.jsonl",
            "11111111-1111-1111-1111-111111111111",
            """{"id":"u1","timestamp":"t","type":"user","content":[{"text":"hi"}]}""");

        var source = new GeminiImportSource(tmpDirOverride: tmp.Path);
        await Assert.That(source.IsAvailable).IsTrue();

        var found = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Vendor).IsEqualTo("gemini");
        // Dashless FULL id from the header — not the 8-char filename shortId.
        await Assert.That(found[0].SessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task discover_filter_by_session_matches_dashless_id() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-a-22222222.jsonl", "22222222-2222-2222-2222-222222222222");
        WriteSession(tmp.Path, "proj", "session-b-33333333.jsonl", "33333333-3333-3333-3333-333333333333");

        var source = new GeminiImportSource(tmpDirOverride: tmp.Path);
        var found  = await source.DiscoverAsync(
            new DiscoveryFilters(null, "22222222-2222-2222-2222-222222222222", null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo("22222222222222222222222222222222");
    }

    [Test]
    public async Task unavailable_when_tmp_dir_missing() {
        using var tmp = new TempDir();
        var source = new GeminiImportSource(tmpDirOverride: tmp.PathTo("does-not-exist"));
        await Assert.That(source.IsAvailable).IsFalse();
    }

    // Mirror of the GeminiTranscriptNormalizer skip rules.
    [Test]
    [Arguments("""{"sessionId":"s","projectHash":"h","kind":"main"}""", false)]                                                              // header
    [Arguments("""{"$set":{"lastUpdated":"t"}}""", false)]                                                                                    // mutation op
    [Arguments("""{"id":"u","type":"user","content":[{"text":"<session_context>x</session_context>"}]}""", false)]                            // bootstrap
    [Arguments("""{"id":"u","type":"user","content":[{"functionResponse":{"id":"f","name":"read","response":{"output":"x"}}}]}""", false)]    // tool-result echo
    [Arguments("""{"id":"u","type":"user","content":[{"text":"real prompt"}]}""", true)]                                                      // real user prompt
    [Arguments("""{"id":"g","type":"gemini","content":"answer"}""", true)]                                                                    // gemini turn
    public async Task import_relevant_line_mirrors_normalizer_skips(string line, bool expected) {
        await Assert.That(GeminiImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }
}
