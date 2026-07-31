using System.Text.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentDecisionLogTests {
    static LaunchConsentRecord Rec(string agent = "a1") => new(
        DateTimeOffset.UtcNow.ToString("O"), agent, "user_x", false,
        "agent", "/tmp/repo", "claude", "denied", "default");

    [Test]
    public async Task Records_append_as_parseable_snake_case_jsonl() {
        var dir = Directory.CreateTempSubdirectory("kcap-cdl-").FullName;
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance);
        log.Record(Rec("a1"));
        log.Record(Rec("a2"));
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        using var parsed = JsonDocument.Parse(lines[0]);
        await Assert.That(parsed.RootElement.GetProperty("agent_id").GetString()).IsEqualTo("a1");
        await Assert.That(parsed.RootElement.GetProperty("outcome").GetString()).IsEqualTo("denied");
    }

    [Test]
    public async Task Rotates_to_backup_at_cap() {
        var dir = Directory.CreateTempSubdirectory("kcap-cdl-").FullName;
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance, maxBytes: 512);
        for (var i = 0; i < 20; i++) log.Record(Rec($"agent-{i}"));
        await Assert.That(File.Exists(Path.Combine(dir, "consent-decisions.jsonl.1"))).IsTrue();
        var live = new FileInfo(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(live.Length <= 512).IsTrue();
    }

    [Test]
    public async Task Unwritable_directory_never_throws() {
        var log = new LaunchConsentDecisionLog("/nonexistent/deeply/nested", NullLogger.Instance);
        log.Record(Rec());
        await Assert.That(true).IsTrue();
    }
}
