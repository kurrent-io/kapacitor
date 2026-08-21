using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DaemonConsentCommandTests {
    [Test]
    public async Task BuildRule_maps_flags_to_rule_fields() {
        var rule = DaemonConsentCommand.TryBuildRule("deny",
            ["--requester", "user_x", "--kind", "review-flow", "--vendor", "codex"], out var error);
        await Assert.That(error).IsNull();
        await Assert.That(rule!.Action).IsEqualTo("deny");
        await Assert.That(rule.Requester).IsEqualTo("user_x");
        await Assert.That(rule.Kind).IsEqualTo("review-flow");
        await Assert.That(rule.Repo).IsNull();
        await Assert.That(rule.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task BuildRule_rejects_flagless_and_unknown_flags_and_bad_kind() {
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", [], out var e1)).IsNull();
        await Assert.That(e1).Contains("at least one");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--nope", "x"], out var e2)).IsNull();
        await Assert.That(e2).Contains("--nope");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--kind", "flows"], out var e3)).IsNull();
        await Assert.That(e3).Contains("kind");
    }

    // NOTE (honest limitation): TryReadLines exists to survive the daemon's check-then-open
    // rotation race (File.Move to .1 leaving a window where the live path doesn't exist) plus a
    // transient Windows sharing-violation IOException. That exact race — another process
    // renaming/holding the file open at the precise moment ReadAllLines runs — isn't
    // deterministically reproducible from an in-process unit test. These tests instead pin the
    // documented, deterministic parts of its contract: a genuinely absent path/directory reads as
    // empty rather than throwing, and a present file with blank lines filtered still reads its
    // real content.

    [Test]
    public async Task TryReadLines_on_a_nonexistent_path_returns_empty_instead_of_throwing() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-consent-log-missing-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        await Assert.That(File.Exists(missing)).IsFalse();

        var lines = DaemonConsentCommand.TryReadLines(missing);
        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task TryReadLines_on_a_nonexistent_directory_returns_empty_instead_of_throwing() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-consent-log-nodir-" + Guid.NewGuid().ToString("N")[..8], "consent-decisions.jsonl");

        var lines = DaemonConsentCommand.TryReadLines(missing);
        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task TryReadLines_filters_blank_lines_from_an_existing_file() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("consent-decisions.jsonl", "{\"a\":1}\n\n{\"a\":2}\n");

        var lines = DaemonConsentCommand.TryReadLines(path);
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("{\"a\":1}");
        await Assert.That(lines[1]).IsEqualTo("{\"a\":2}");
    }
}
