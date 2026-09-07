namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

using Capacitor.Models.Transcripts.Harness.Claude;

public class ClaudeNativeTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Transcript(params string[] lines) =>
        Tmp.CreateFile($"{Guid.NewGuid():N}.jsonl", string.Join('\n', lines) + "\n");

    [Test]
    public async Task Last_ai_title_wins() {
        var path = Transcript(
            """{"type":"ai-title","aiTitle":"First cut","sessionId":"s1"}""",
            """{"type":"user","message":{"content":"hi"}}""",
            """{"type":"ai-title","aiTitle":"Final title","sessionId":"s1"}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("Final title");
    }

    [Test]
    public async Task Legacy_summary_shape_is_accepted() {
        var path = Transcript("""{"type":"summary","summary":"Legacy title","leafUuid":"u1"}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("Legacy title");
    }

    [Test]
    public async Task Later_line_wins_across_shapes() {
        var path = Transcript(
            """{"type":"summary","summary":"Old shape","leafUuid":"u1"}""",
            """{"type":"ai-title","aiTitle":"New shape","sessionId":"s1"}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("New shape");
    }

    [Test]
    public async Task Returns_null_without_any_title_line() {
        var path = Transcript(
            """{"type":"mode","mode":"normal","sessionId":"s1"}""",
            """{"type":"user","message":{"content":"hi"}}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsNull();
    }

    [Test]
    public async Task Returns_null_for_missing_file() {
        await Assert.That(ClaudeNativeTitle.TryExtract(Tmp.PathTo("absent.jsonl"))).IsNull();
    }

    [Test]
    public async Task Malformed_lines_are_skipped() {
        var path = Transcript(
            """{"type":"ai-title","aiTitle":"Good title","sessionId":"s1"}""",
            """{"type":"ai-title", broken json""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("Good title");
    }

    [Test]
    public async Task Blank_title_lines_are_ignored() {
        var path = Transcript(
            """{"type":"ai-title","aiTitle":"Real title","sessionId":"s1"}""",
            """{"type":"ai-title","aiTitle":"   ","sessionId":"s1"}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("Real title");
    }

    [Test]
    public async Task Long_titles_are_capped_at_120() {
        var longTitle = new string('x', 200);
        var path = Transcript($$"""{"type":"ai-title","aiTitle":"{{longTitle}}","sessionId":"s1"}""");

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo(new string('x', 120));
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Transcript("""{"type":"ai-title","aiTitle":"Live title","sessionId":"s1"}""");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(ClaudeNativeTitle.TryExtract(path)).IsEqualTo("Live title");
    }
}
