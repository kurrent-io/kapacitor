using System.Globalization;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class EndedAtResolverTests : IDisposable {
    readonly string _dir = Directory.CreateTempSubdirectory("kcap-endedat").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Test]
    public async Task Copilot_shutdown_is_the_end_even_when_last_line() {
        var path = Path.Combine(_dir, "events.jsonl");
        File.WriteAllLines(path, new[] {
            """{"type":"user.message","timestamp":"2026-06-12T10:00:00.000Z","data":{"content":"hi"}}""",
            """{"type":"assistant.message","timestamp":"2026-06-12T10:00:05.000Z","data":{"content":"yo"}}""",
            """{"type":"session.shutdown","timestamp":"2026-06-12T10:00:30.000Z","data":{}}""",
        });

        var ts = EndedAtResolvers.CopilotShutdownTimestamp(path);

        await Assert.That(ts).IsNotNull();
        await Assert.That(ts!.Value).IsEqualTo(DateTimeOffset.Parse("2026-06-12T10:00:30.000Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task LastTimestamp_scans_tail_for_last_timestamped_record() {
        var path = Path.Combine(_dir, "s.jsonl");
        File.WriteAllLines(path, new[] {
            """{"timestamp":"2026-06-12T10:00:00.000Z"}""",
            """{"no_timestamp":true}""",
            """{"timestamp":"2026-06-12T10:09:00.000Z"}""",
        });

        var ts = EndedAtResolvers.LastTimestampFromJsonl(path);

        await Assert.That(ts!.Value).IsEqualTo(DateTimeOffset.Parse("2026-06-12T10:09:00.000Z", CultureInfo.InvariantCulture));
    }
}
