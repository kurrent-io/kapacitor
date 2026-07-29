using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// <see cref="DaemonRunner.ParseProbedVersion"/> — the pure half of the vendor CLI <c>--version</c>
/// probe whose result the daemon advertises as <c>cli_version</c> in its capability payload (read by
/// the borrowed-review pre-flight) and feeds to <see cref="DaemonRunner.CliVersionAllowed"/>.
///
/// <para>The live defect: GitHub Copilot CLI prints its version at the end of a sentence and then a
/// second "update available" line. Tokenising on spaces alone glued the newline and the next line's
/// first word onto the version, advertising <c>"1.0.75.\nRun"</c> — which no version parser accepts.
/// Each case below is real observed output from the installed CLI.</para>
/// </summary>
public class DaemonRunnerVersionProbeTests {
    [Test]
    public async Task Copilot_multiline_output_yields_just_the_version() {
        const string observed = "GitHub Copilot CLI 1.0.75.\nRun 'copilot update' to check for updates.";

        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsEqualTo("1.0.75");
    }

    [Test]
    public async Task Copilot_version_is_usable_by_the_range_check() {
        // Not merely cosmetic: the advertised string has to parse, or every certification range
        // check for this vendor fails closed on a perfectly good build.
        var parsed = DaemonRunner.ParseProbedVersion("GitHub Copilot CLI 1.0.75.\nRun 'copilot update' to check for updates.");

        await Assert.That(DaemonRunner.CliVersionAllowed(parsed, ">=1.0.0")).IsTrue();
    }

    [Test]
    public async Task Windows_line_endings_are_also_separators() {
        await Assert.That(DaemonRunner.ParseProbedVersion("GitHub Copilot CLI 1.0.75.\r\nRun 'copilot update'."))
            .IsEqualTo("1.0.75");
    }

    [Test]
    [Arguments("2.1.212 (Claude Code)", "2.1.212")]        // claude
    [Arguments("codex-cli 0.144.3", "0.144.3")]            // codex — first token has no digits
    [Arguments("2026.07.23-e383d2b", "2026.07.23-e383d2b")] // cursor-agent
    [Arguments("0.50.0", "0.50.0")]                         // gemini
    [Arguments("v1.2.3", "1.2.3")]                          // a "v"-prefixed build
    public async Task Single_line_vendor_output_is_unchanged(string observed, string expected) {
        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("command not found")]
    public async Task Output_with_no_version_token_resolves_to_null(string observed) {
        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsNull();
    }
}
