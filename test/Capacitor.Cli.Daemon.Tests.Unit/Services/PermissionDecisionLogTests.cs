using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionDecisionLogTests {
    static PermissionDecisionRecord Rec(string agent = "a1") =>
        new(DateTimeOffset.UtcNow.ToString("O"), agent, "s1", "claude", "Bash", "allow", "app");

    [Test]
    public async Task Records_append_as_parseable_snake_case_jsonl_owner_only() {
        using var tmp = new TempDir();
        var log = new PermissionDecisionLog(tmp.Path, NullLogger.Instance);
        log.Record(Rec("a1"));
        log.Record(Rec("a2"));
        var path = tmp.PathTo("permission-decisions.jsonl");
        var lines = File.ReadAllLines(path);
        await Assert.That(lines.Length).IsEqualTo(2);
        using var parsed = JsonDocument.Parse(lines[1]);
        await Assert.That(parsed.RootElement.GetProperty("agent_id").GetString()).IsEqualTo("a2");
        await Assert.That(parsed.RootElement.GetProperty("source").GetString()).IsEqualTo("app");
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Rotates_to_backup_at_cap() {
        using var tmp = new TempDir();
        var log = new PermissionDecisionLog(tmp.Path, NullLogger.Instance, maxBytes: 512);
        for (var i = 0; i < 20; i++) log.Record(Rec($"agent-{i}"));
        await Assert.That(File.Exists(tmp.PathTo("permission-decisions.jsonl.1"))).IsTrue();
        await Assert.That(new FileInfo(tmp.PathTo("permission-decisions.jsonl")).Length <= 512).IsTrue();
    }

    [Test]
    public async Task Unwritable_directory_never_throws() {
        var log = new PermissionDecisionLog("/nonexistent/deeply/nested", NullLogger.Instance);
        await Assert.That(() => log.Record(Rec())).ThrowsNothing();
    }
}
