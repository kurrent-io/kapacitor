using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class BootRefusalTests {
    [Test]
    public async Task Write_then_read_round_trips_identity() {
        var dir = Directory.CreateTempSubdirectory("refusal-").FullName;
        var config = new DaemonConfig {
            Name = "d1", ExpectedServerUrl = "https://a", ServerUrl = "https://b",
            InstanceId = "i-1", BootAttemptId = "att-9",
        };
        BootRefusal.TryWrite(dir, config, "server_expectation_mismatch");

        var r = BootRefusal.TryRead(dir);
        await Assert.That(r!.Token).IsEqualTo("server_expectation_mismatch");
        await Assert.That(r.DaemonName).IsEqualTo("d1");
        await Assert.That(r.Expectation).IsEqualTo("https://a");
        await Assert.That(r.Resolved).IsEqualTo("https://b");
        await Assert.That(r.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(r.AttemptId).IsEqualTo("att-9");
    }

    [Test]
    public async Task Write_into_unwritable_dir_is_swallowed() {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("refusal-").FullName, "missing", "deep");
        // no Directory.CreateDirectory — TryWrite must not throw
        BootRefusal.TryWrite(dir, new DaemonConfig { Name = "d" }, "consent_seed_unwritable");
        await Assert.That(BootRefusal.TryRead(dir)).IsNull();
    }

    [Test]
    public async Task Corrupt_marker_is_renamed_aside_and_reads_null() {
        var dir = Directory.CreateTempSubdirectory("refusal-").FullName;
        await File.WriteAllTextAsync(BootRefusal.MarkerPath(dir), "{corrupt");
        await Assert.That(BootRefusal.TryRead(dir)).IsNull();
        await Assert.That(File.Exists(BootRefusal.MarkerPath(dir))).IsFalse();
        await Assert.That(Directory.GetFiles(dir, "boot-refusal.json.quarantined-*")).IsNotEmpty();
    }

    [Test]
    public async Task Expectation_comparison_normalizes_trailing_slash_and_case() {
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://S.example/", "https://s.example")).IsTrue();
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://a.example", "https://b.example")).IsFalse();
        await Assert.That(DaemonRunner.ExpectationSatisfied(null, "https://b.example")).IsTrue(); // no expectation
    }
}
