using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTextTests {
    [Test]
    public async Task Xml_escapes_the_five_markup_chars() {
        await Assert.That(ServiceText.Xml("a&b<c>\"d'")).IsEqualTo("a&amp;b&lt;c&gt;&quot;d&apos;");
    }

    [Test]
    public async Task CmdValue_doubles_percent_signs() {
        await Assert.That(ServiceText.CmdValue("100%PATH%")).IsEqualTo("100%%PATH%%");
    }

    /// <summary>
    /// SystemdValue no longer rewrites newlines to spaces — RequireNoControlCharacters refuses them at the
    /// sink first, so the replacement was unreachable, and silently rewriting a caller's value was the wrong
    /// behaviour: a service running with a value nobody chose is harder to diagnose than a failed install.
    /// </summary>
    [Test]
    public async Task SystemdValue_no_longer_rewrites_control_characters() {
        await Assert.That(ServiceText.SystemdValue("a\nb")).IsEqualTo("a\nb");
    }
}
