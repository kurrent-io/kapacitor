using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Core.Tests.Unit.Curation;

public class CuratedTextNormalizerTests {
    [Test]
    public async Task Collapses_newlines_to_single_line() {
        var r = CuratedTextNormalizer.Normalize("line one\r\n  line two\n\nline three");
        await Assert.That(r).IsEqualTo("line one line two line three");
    }

    [Test]
    public async Task Defangs_html_comment_terminator() {
        var r = CuratedTextNormalizer.Normalize("text with <!-- kcap:curated:end --> inside");
        await Assert.That(r!.Contains("-->")).IsFalse();
    }

    [Test]
    public async Task Empty_or_whitespace_returns_null() {
        await Assert.That(CuratedTextNormalizer.Normalize("   \n\t ")).IsNull();
        await Assert.That(CuratedTextNormalizer.Normalize(null)).IsNull();
    }
}
