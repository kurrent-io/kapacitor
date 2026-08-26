using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Pins the two disclosure surfaces this repo owns.
///
/// <para>The spec's own observation is that neither is checked by code — which is exactly how
/// "anonymous" survived becoming false. So this is the check. Once one bridge exists, a random
/// device id is linkable to an identified person, which means CLI telemetry ALREADY COLLECTED
/// under the old wording becomes attributable retroactively: the dataset's character changes, not
/// merely the new events.</para>
///
/// <para>Note the first-run notice fires once per machine and records that it has, so anyone who
/// already saw the old wording will never be shown the new one. That is why the README carries the
/// weight here, not the notice.</para>
/// </summary>
public class DisclosureWordingTests {
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    [Test]
    public async Task The_first_run_notice_says_pseudonymous_not_anonymous() {
        var source = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Capacitor.Cli.Core", "Telemetry", "CliTelemetry.cs"));

        await Assert.That(source).Contains("pseudonymous usage data");
        await Assert.That(source).DoesNotContain("anonymous usage data");
    }

    [Test]
    public async Task The_README_telemetry_section_says_pseudonymous_and_names_the_association() {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "README.md"));
        var start  = readme.IndexOf("### Telemetry", StringComparison.Ordinal);

        await Assert.That(start).IsGreaterThan(-1);

        var end     = readme.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = end > start ? readme[start..end] : readme[start..];

        await Assert.That(section).Contains("pseudonymous");
        await Assert.That(section).DoesNotContain("anonymous usage data");
        await Assert.That(section).Contains("web_device_id");
        await Assert.That(section).Contains("join_id");
    }

    // A third surface neither the spec nor the plan listed: `kcap config` help text, which the
    // repo's own rule counts as user-facing CLI surface. It described the data as anonymous too.
    [Test]
    public async Task The_config_help_text_does_not_still_call_it_anonymous() {
        var help = await File.ReadAllTextAsync(Path.Combine(
            RepoRoot(), "src", "Capacitor.Cli.Core", "Resources", "help-config.txt"));

        await Assert.That(help).DoesNotContain("Anonymous");
        await Assert.That(help).Contains("Pseudonymous CLI usage reporting");
    }

    // The claim that has to survive: opting out suppresses the whole mechanism, not just the events.
    [Test]
    public async Task The_README_still_promises_that_opting_out_suppresses_all_of_it() {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "README.md"));
        var start  = readme.IndexOf("### Telemetry", StringComparison.Ordinal);
        var end    = readme.IndexOf("\n## ", start, StringComparison.Ordinal);
        var section = end > start ? readme[start..end] : readme[start..];

        await Assert.That(section).Contains("Opting out");
    }
}
