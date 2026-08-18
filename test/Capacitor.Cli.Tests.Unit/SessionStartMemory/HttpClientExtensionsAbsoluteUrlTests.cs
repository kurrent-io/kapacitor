using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class HttpClientExtensionsAbsoluteUrlTests {
    [Test]
    [Arguments("https://staging.kcap.ai/hooks/stop")]
    [Arguments("http://localhost:5108/hooks/stop")]
    [Arguments("http://127.0.0.1:5108")]
    public async Task Accepts_AbsoluteHttpAndHttps(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsTrue();
    }

    [Test]
    [Arguments("staging.kcap.ai/hooks/stop")]
    [Arguments("/hooks/stop")]
    [Arguments("")]
    [Arguments("not a url at all")]
    public async Task Rejects_RelativeOrMalformed(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("ftp://example.com")]
    [Arguments("javascript:alert(1)")]
    public async Task Rejects_NonHttpSchemes(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    /// <summary>
    /// <see cref="HookHttp.IsPostable"/> is the single predicate every hook-path guard consults.
    /// Covers all four unusable classes — whitespace, scheme-less, relative, and absolute
    /// wrong-scheme. The last is named explicitly (<c>ftp://host</c>, <c>file:///etc/passwd</c>)
    /// because an implementation validating only <c>UriKind.Absolute</c> would accept it.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative")]
    [Arguments("not a url at all")]
    [Arguments("ftp://host")]
    [Arguments("file:///etc/passwd")]
    public async Task IsPostable_rejects_every_unusable_form(string? url) {
        await Assert.That(HookHttp.IsPostable(url)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task IsPostable_accepts_absolute_http(string url) {
        await Assert.That(HookHttp.IsPostable(url)).IsTrue();
    }

    /// <summary>
    /// The two named guards that predate <see cref="HookHttp"/> must stay byte-identical to it.
    /// They kept their names and their existing coverage but now delegate; this pins that they
    /// cannot drift back apart.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative")]
    [Arguments("ftp://host")]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task Delegating_predicates_agree_with_IsPostable(string? url) {
        var expected = HookHttp.IsPostable(url);

        await Assert.That(SessionStartMemoryHookSupport.CanAttempt(url)).IsEqualTo(expected);
        await Assert.That(CodexHookCommand.CanAttemptMemoryInjection(url)).IsEqualTo(expected);
    }
}
