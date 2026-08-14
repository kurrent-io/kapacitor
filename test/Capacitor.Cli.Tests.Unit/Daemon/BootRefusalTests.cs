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

    // Pins the fix for a real gap: on a brand-new daemon name, nothing had created stateDir before
    // an expectation-mismatch refusal fired (LaunchConsentStore's ctor — the only prior creator —
    // never runs on that arm). DaemonRunner.RunAsync's boot-check block now best-effort-creates
    // coverageStateDir up front (Directory.CreateDirectory, swallowed on failure) before either
    // check; this test pins the postcondition that establishes — TryWrite persisting a marker for
    // an expectation mismatch into a dir that did not exist a moment ago — without spinning up a
    // real host (RunAsync itself isn't practically unit-testable end to end).
    [Test]
    public async Task Expectation_mismatch_marker_persists_once_the_fresh_state_dir_is_pre_created() {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("refusal-").FullName, "brand-new-name");
        await Assert.That(Directory.Exists(dir)).IsFalse();

        Directory.CreateDirectory(dir); // mirrors DaemonRunner's eager best-effort creation
        var config = new DaemonConfig { Name = "d2", ExpectedServerUrl = "https://a", ServerUrl = "https://b" };
        BootRefusal.TryWrite(dir, config, "server_expectation_mismatch");

        var r = BootRefusal.TryRead(dir);
        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Token).IsEqualTo("server_expectation_mismatch");
    }

    // ── RunBootChecksAsync: the extracted, directly-testable pre-host boot-check block ──

    [Test]
    public async Task RunBootChecksAsync_null_directive_is_truly_absent_and_proceeds() {
        var dir = Directory.CreateTempSubdirectory("bootcheck-").FullName;
        var config = new DaemonConfig { Name = "d-absent", ServerUrl = "https://s", ConsentSeedDirective = null };

        var exit = await DaemonRunner.RunBootChecksAsync(config, dir);

        await Assert.That(exit).IsNull();
        await Assert.That(File.Exists(Path.Combine(dir, "consent.json"))).IsFalse(); // never seeded
    }

    [Test, NotInParallel]
    public async Task RunBootChecksAsync_empty_directive_activates_the_seed_path_and_refuses_invalid() {
        var dir = Directory.CreateTempSubdirectory("bootcheck-").FullName;
        // Empty is a deliberate refusal under the exact-value contract, not absence — the seed path
        // must activate on it (BootSeed("") itself already classifies RefusedInvalidDirective).
        var config = new DaemonConfig { Name = "d-empty", ServerUrl = "https://s", ConsentSeedDirective = "" };
        var originalErr = Console.Error;
        var captured = new StringWriter();
        try {
            Console.SetError(captured);
            var exit = await DaemonRunner.RunBootChecksAsync(config, dir);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured.ToString()).Contains("consent_seed_invalid");
        } finally { Console.SetError(originalErr); }
    }

    [Test, NotInParallel]
    public async Task RunBootChecksAsync_unwritable_state_dir_with_directive_yields_coded_refusal_not_a_throw() {
        // A file sitting exactly where the state directory belongs makes the seed path fail
        // (whether LaunchConsentStore's own ctor throws directly, or construction succeeds but
        // Persist()'s own I/O against the bogus path fails) — either way this must land as the
        // coded consent_seed_unwritable refusal, never an uncoded crash that respins under KeepAlive.
        var parent = Directory.CreateTempSubdirectory("bootcheck-").FullName;
        var stateDirAsFile = Path.Combine(parent, "state-is-a-file");
        await File.WriteAllTextAsync(stateDirAsFile, "not a directory");
        var config = new DaemonConfig { Name = "d-unwritable", ServerUrl = "https://s", ConsentSeedDirective = "prompt" };
        var originalErr = Console.Error;
        var captured = new StringWriter();
        try {
            Console.SetError(captured);
            var exit = await DaemonRunner.RunBootChecksAsync(config, stateDirAsFile);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured.ToString()).Contains("consent_seed_unwritable");
        } finally { Console.SetError(originalErr); }
    }
}
