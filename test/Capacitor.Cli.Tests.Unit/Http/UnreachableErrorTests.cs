using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Http;

/// <summary>
/// The "server unreachable" stderr line. It echoed the base URL raw, while its sibling
/// <see cref="UnusableUrlDiagnostic"/> — same assembly, same stream, same hazard — had sanitized for
/// exactly this since it was written.
///
/// <para>Two things ride on the sanitization. A <c>server_url</c> may carry userinfo credentials, and
/// this line is reachable from the hook path (<c>AgentHookPoster.PostAsync</c> on any transport fault),
/// so an unreachable server printed them on every lifecycle POST for every vendor. And a control
/// character anywhere in the rendered line can fabricate a second line in a stream harnesses parse —
/// Gemini in particular reads hook stderr as the hook's own output when stdout is empty.</para>
/// </summary>
public class UnreachableErrorTests {
    [Test]
    public async Task The_line_drops_credentials_from_the_url() {
        var line = HttpClientExtensions.RenderUnreachableError(
            "https://user:sup3rs3cret@kcap.example.com", "No such host is known.");

        await Assert.That(line).DoesNotContain("sup3rs3cret");
        await Assert.That(line).DoesNotContain("user:");
    }

    /// <summary>The complement, and the reason this cannot be "sanitize to nothing": the whole point of
    /// the line is naming WHICH server was unreachable, so the host and the cause must survive.</summary>
    [Test]
    public async Task The_line_still_names_the_host_and_the_cause() {
        var line = HttpClientExtensions.RenderUnreachableError(
            "https://user:sup3rs3cret@kcap.example.com", "No such host is known.");

        await Assert.That(line).Contains("kcap.example.com");
        await Assert.That(line).Contains("No such host is known.");
    }

    /// <summary>A credential can hide behind a second '@' — take the LAST one, as Sanitize does.</summary>
    [Test]
    [Arguments("https://user:sup3rs3cret@")]
    [Arguments("https://a@b:sup3rs3cret@host")]
    public async Task The_line_drops_credentials_in_the_awkward_shapes_too(string url) {
        await Assert.That(HttpClientExtensions.RenderUnreachableError(url, "boom")).DoesNotContain("sup3rs3cret");
    }

    /// <summary>
    /// No VARIABLE component may fabricate a line — not the URL, and not the exception message either.
    /// Guarding only the URL would leave the other half of the interpolation open.
    ///
    /// <para>Asserted as "the payload never begins a line" rather than "the output holds no control
    /// character", because <c>UnreachableHint</c> itself contains a literal <c>\r</c>. A blanket
    /// no-control-character assertion would be testing that fixed prefix, which no attacker can reach,
    /// and would fail for a reason that has nothing to do with injection.</para>
    /// </summary>
    [Test]
    [Arguments("https://host\r\n[kcap] everything is fine", "boom")]
    [Arguments("https://host", "boom\r\n[kcap] everything is fine")]
    public async Task No_variable_component_can_inject_a_second_line(string url, string message) {
        var line = HttpClientExtensions.RenderUnreachableError(url, message);

        // A newline is what actually splits a line for a line-oriented reader, and the hint has none.
        await Assert.That(line).DoesNotContain("\n");

        // Belt and braces against a lone \r on a terminal: the payload must not open a segment either.
        var segments = line.Split('\n', '\r');
        await Assert.That(segments.Any(s => s.StartsWith("[kcap] everything is fine", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task A_blank_url_renders_without_throwing(string? url) {
        await Assert.That(HttpClientExtensions.RenderUnreachableError(url, "boom")).IsNotEmpty();
    }
}
