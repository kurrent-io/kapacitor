using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LaunchConsentDecisionLogTests {
    static ConsentDecisionRecord Rec(string agent = "a1") => new(
        DateTimeOffset.UtcNow.ToString("O"), agent, "user_x", false,
        "agent", "/tmp/repo", "claude", "denied", "default", "Mathias");

    [Test]
    public async Task Records_append_as_parseable_snake_case_jsonl() {
        using var tmp = new TempDir();
        var log = new LaunchConsentDecisionLog(tmp.Path, NullLogger.Instance);
        log.Record(Rec("a1"));
        log.Record(Rec("a2"));
        var lines = File.ReadAllLines(tmp.PathTo("consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        using var parsed = JsonDocument.Parse(lines[0]);
        await Assert.That(parsed.RootElement.GetProperty("agent_id").GetString()).IsEqualTo("a1");
        await Assert.That(parsed.RootElement.GetProperty("outcome").GetString()).IsEqualTo("denied");
        await Assert.That(lines[0]).Contains("\"requester_display\":\"Mathias\"");
    }

    [Test]
    public async Task Rotates_to_backup_at_cap() {
        using var tmp = new TempDir();
        var log = new LaunchConsentDecisionLog(tmp.Path, NullLogger.Instance, maxBytes: 512);
        for (var i = 0; i < 20; i++) log.Record(Rec($"agent-{i}"));
        await Assert.That(File.Exists(tmp.PathTo("consent-decisions.jsonl.1"))).IsTrue();
        var live = new FileInfo(tmp.PathTo("consent-decisions.jsonl"));
        await Assert.That(live.Length <= 512).IsTrue();
    }

    [Test]
    public async Task Unwritable_directory_never_throws() {
        var log = new LaunchConsentDecisionLog("/nonexistent/deeply/nested", NullLogger.Instance);
        await Assert.That(() => log.Record(Rec())).ThrowsNothing();
    }

    [Test]
    public async Task Record_creates_an_owner_only_file_in_an_owner_only_directory() {
        // The log carries repo paths and requester ids, so it must not be world/group readable.
        // Unix-only: file modes are a no-op on Windows.
        if (OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var log = new LaunchConsentDecisionLog(tmp.Path, NullLogger.Instance);
        log.Record(Rec("agent-perms"));

        const UnixFileMode ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode ownerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        var logFile = tmp.PathTo("consent-decisions.jsonl");
        await Assert.That(File.GetUnixFileMode(logFile)).IsEqualTo(ownerOnlyFile);
        await Assert.That(File.GetUnixFileMode(tmp.Path)).IsEqualTo(ownerOnlyDir);
    }
}
