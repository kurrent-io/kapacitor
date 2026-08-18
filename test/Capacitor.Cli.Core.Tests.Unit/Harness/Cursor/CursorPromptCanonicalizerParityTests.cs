using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorPromptCanonicalizerParityTests {
    // Byte-identical corpus to CursorPromptCanonicalizerTests.Corpus() on the server.
    public static IEnumerable<(string Raw, string Canonical)> Corpus() {
        yield return ("<user_query>\nfix the bug\n</user_query>", "fix the bug");
        yield return ("<timestamp>Wednesday, Jul 8, 2026, 9:48 AM (UTC-4)</timestamp>\n<user_query>\nfix the bug\n</user_query>", "fix the bug");
        yield return ("<user_query>\r\nfix the bug\r\n</user_query>", "fix the bug");
        yield return ("   <user_query>\nfix the bug\n</user_query>   ", "fix the bug");
        yield return ("<user_query>\n  indented body\n</user_query>", "  indented body");
        yield return ("<user_query>\nline1\n  line2  \n</user_query>", "line1\n  line2  ");
        yield return ("", "");
        yield return ("<user_query>\n</user_query>", "");
        yield return ("plain text, no wrapper", "plain text, no wrapper");
        yield return ("<user_query>unterminated", "<user_query>unterminated");
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Canonicalize_matches_corpus(string raw, string canonical) {
        await Assert.That(CursorPromptCanonicalizer.Canonicalize(raw)).IsEqualTo(canonical);
    }
}
