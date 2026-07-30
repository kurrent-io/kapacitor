using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Http;

/// <summary>
/// The one stderr line a guard writes. It must name the right thing to fix and must never echo the
/// URL raw: a server_url can carry credentials, and an embedded newline would let it inject a
/// fabricated line into a stream harnesses parse.
/// </summary>
public class UnusableUrlDiagnosticTests {
    [Test]
    public async Task Sanitize_drops_credentials() {
        var s = UnusableUrlDiagnostic.Sanitize("user:sup3rs3cret@evil.example.com/path");

        await Assert.That(s).DoesNotContain("sup3rs3cret");
        await Assert.That(s).DoesNotContain("user:");
        await Assert.That(s).Contains("evil.example.com");
    }

    [Test]
    public async Task Sanitize_strips_control_characters_so_a_line_cannot_be_injected() {
        var s = UnusableUrlDiagnostic.Sanitize("localhost:5108\r\n[kcap] everything is fine");

        await Assert.That(s).DoesNotContain("\n");
        await Assert.That(s).DoesNotContain("\r");
        await Assert.That(s).DoesNotContain("");
    }

    [Test]
    public async Task Sanitize_caps_length() {
        var s = UnusableUrlDiagnostic.Sanitize("host" + new string('x', 5_000));

        await Assert.That(s.Length).IsLessThanOrEqualTo(81); // 80 + the ellipsis
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Sanitize_renders_a_blank_url_without_throwing(string? url) {
        await Assert.That(UnusableUrlDiagnostic.Sanitize(url)).IsEqualTo("(empty)");
    }

    /// <summary>
    /// `kcap config set server_url` does NOT repair a malformed KCAP_URL or --server-url — both
    /// outrank the profile — so a single fixed remediation string would be actively wrong for two of
    /// the three sources named in the problem this fixes.
    /// </summary>
    [Test]
    [Arguments(UrlSource.CommandLine, "--server-url")]
    [Arguments(UrlSource.Environment, "KCAP_URL")]
    [Arguments(UrlSource.Profile,     "kcap config set server_url")]
    public async Task Build_names_the_source_specific_remediation(UrlSource source, string expected) {
        var msg = UnusableUrlDiagnostic.Build(source, "localhost:5108", "session-start/codex spooled, not sent");

        await Assert.That(msg).Contains(expected);
        await Assert.That(msg).Contains("session-start/codex spooled, not sent");
    }

    [Test]
    public async Task Build_never_leaks_the_raw_url() {
        var msg = UnusableUrlDiagnostic.Build(UrlSource.Environment, "tok3n:s3cret@host\r\ninjected", "dropped");

        await Assert.That(msg).DoesNotContain("s3cret");
        await Assert.That(msg).DoesNotContain("injected\n");
    }
}
