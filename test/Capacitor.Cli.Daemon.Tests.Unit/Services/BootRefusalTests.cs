using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class BootRefusalTests {
    static void Write(DaemonConfig config, string token) =>
        BootRefusalMarker.TryWrite(
            config.Store, config.Name, token, config.ExpectedServerUrl, config.ServerUrl,
            config.InstanceId, config.BootAttemptId);

    [Test]
    public async Task Write_then_read_round_trips_identity() {
        using var tmp = new TempDir();
        var config = new DaemonConfig {
            Name = "d1", ExpectedServerUrl = "https://a", ServerUrl = "https://b",
            InstanceId = "i-1", BootAttemptId = "att-9", Store = new DaemonStore(tmp.Path),
        };
        Directory.CreateDirectory(config.Store.StateDirectory(config.Name));
        Write(config, "server_expectation_mismatch");

        var r = BootRefusalMarker.TryRead(config.Store, config.Name);
        await Assert.That(r!.Token).IsEqualTo("server_expectation_mismatch");
        await Assert.That(r.DaemonName).IsEqualTo("d1");
        await Assert.That(r.Expectation).IsEqualTo("https://a");
        await Assert.That(r.Resolved).IsEqualTo("https://b");
        await Assert.That(r.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(r.AttemptId).IsEqualTo("att-9");
    }

    [Test]
    public async Task Write_into_unwritable_dir_is_swallowed() {
        using var tmp = new TempDir();
        var config = new DaemonConfig { Name = "d", Store = new DaemonStore(tmp.PathTo("missing", "deep")) };
        // no Directory.CreateDirectory — the write must not throw
        Write(config, "consent_seed_unwritable");
        await Assert.That(BootRefusalMarker.TryRead(config.Store, config.Name)).IsNull();
    }

    [Test]
    public async Task Expectation_comparison_normalizes_trailing_slash_and_case() {
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://S.example/", "https://s.example")).IsTrue();
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://a.example", "https://b.example")).IsFalse();
        await Assert.That(DaemonRunner.ExpectationSatisfied(null, "https://b.example")).IsTrue(); // no expectation
    }

    // A present-but-empty expectation is a deliberate value under the exact-value contract, not
    // absence — only a genuinely null expectation is absence. Empty must MISMATCH.
    [Test]
    public async Task Empty_expectation_is_present_and_mismatches() {
        await Assert.That(DaemonRunner.ExpectationSatisfied("", "https://b.example")).IsFalse();
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
        using var tmp = new TempDir();
        var dir = tmp.PathTo("brand-new-name");
        await Assert.That(Directory.Exists(dir)).IsFalse();

        var config = new DaemonConfig {
            Name = "d2", ExpectedServerUrl = "https://a", ServerUrl = "https://b",
            Store = new DaemonStore(dir),
        };
        Directory.CreateDirectory(config.Store.StateDirectory(config.Name)); // mirrors DaemonRunner's eager best-effort creation
        Write(config, "server_expectation_mismatch");

        var r = BootRefusalMarker.TryRead(config.Store, config.Name);
        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Token).IsEqualTo("server_expectation_mismatch");
    }

    // ── RunBootChecksAsync: the extracted, directly-testable pre-host boot-check block ──

    [Test, NotInParallel]
    public async Task RunBootChecksAsync_empty_expected_server_url_refuses_as_mismatch() {
        using var tmp = new TempDir();
        var config = new DaemonConfig { Name = "d-empty-expect", ServerUrl = "https://s", ExpectedServerUrl = "", Store = new DaemonStore(tmp.Path) };
        using var capture = ConsoleOutput.StartErrorCapture();
        var exit = await DaemonRunner.RunBootChecksAsync(config);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedError()).Contains("server_expectation_mismatch");
    }

    [Test]
    public async Task RunBootChecksAsync_null_directive_is_truly_absent_and_proceeds() {
        using var tmp = new TempDir();
        var config = new DaemonConfig { Name = "d-absent", ServerUrl = "https://s", ConsentSeedDirective = null, Store = new DaemonStore(tmp.Path) };

        var exit = await DaemonRunner.RunBootChecksAsync(config);

        await Assert.That(exit).IsNull();
        await Assert.That(File.Exists(Path.Combine(config.Store.StateDirectory(config.Name), "consent.json"))).IsFalse(); // never seeded
    }

    [Test, NotInParallel]
    public async Task RunBootChecksAsync_empty_directive_activates_the_seed_path_and_refuses_invalid() {
        using var tmp = new TempDir();
        // Empty is a deliberate refusal under the exact-value contract, not absence — the seed path
        // must activate on it (BootSeed("") itself already classifies RefusedInvalidDirective).
        var config = new DaemonConfig { Name = "d-empty", ServerUrl = "https://s", ConsentSeedDirective = "", Store = new DaemonStore(tmp.Path) };
        using var capture = ConsoleOutput.StartErrorCapture();
        var exit = await DaemonRunner.RunBootChecksAsync(config);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedError()).Contains("consent_seed_invalid");
    }

    [Test, NotInParallel]
    public async Task RunBootChecksAsync_unwritable_state_dir_with_directive_yields_coded_refusal_not_a_throw() {
        // A file sitting exactly where the state directory belongs makes the seed path fail
        // (whether LaunchConsentStore's own ctor throws directly, or construction succeeds but
        // Persist()'s own I/O against the bogus path fails) — either way this must land as the
        // coded consent_seed_unwritable refusal, never an uncoded crash that respins under KeepAlive.
        using var tmp = new TempDir();
        var config = new DaemonConfig {
            Name = "d-unwritable", ServerUrl = "https://s", ConsentSeedDirective = "prompt",
            Store = new DaemonStore(tmp.Path),
        };
        await File.WriteAllTextAsync(config.Store.StateDirectory(config.Name), "not a directory");
        using var capture = ConsoleOutput.StartErrorCapture();
        var exit = await DaemonRunner.RunBootChecksAsync(config);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedError()).Contains("consent_seed_unwritable");
    }
}
