using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

using Capacitor.Tests.Helpers;
namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Boot-refusal marker attribution in <see cref="ServiceVerify"/>'s
/// gated readiness-timeout path. The pure <see cref="ServiceVerify.Attributable"/> rule is exercised
/// verbatim per the task brief; the <see cref="FakeServiceManager"/>-driven tests exercise the
/// verified pre-clear + observed-pid correlation end to end, planting a marker with the daemon's own
/// <see cref="BootRefusal.TryWrite"/> writer so the CLI-side <see cref="BootRefusalReader"/> proves it
/// can actually parse what the daemon actually writes.</summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class BootRefusalAttributionTests {
    const string Id = "boot-refusal-svc";

    // ── pure rule, verbatim per the task brief ──

    [Test]
    public async Task Marker_with_matching_name_expectation_and_observed_pid_attributes() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsTrue();
    }

    [Test]
    public async Task Foreign_pid_never_attributes_even_with_same_name_and_expectation() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 9999, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Different_daemon_name_never_attributes() {
        var e = new BootRefusalEvidence("other", "consent_seed_unwritable", null, null, 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", null, new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Attempt_id_bearing_marker_is_detached_evidence_not_service_evidence() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", "att-1");
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Trailing_slash_difference_still_attributes() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s/", new HashSet<int> { 4242 })).IsTrue();
    }

    [Test]
    public async Task Case_only_difference_still_attributes() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://S.Example", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s.example", new HashSet<int> { 4242 })).IsTrue();
    }

    // A present-but-empty expectation on one side is a deliberate value, never a trivial agreement
    // with genuine absence (null) on the other — only a null/null pair is absence.
    [Test]
    public async Task Null_versus_empty_expectation_never_attributes() {
        var e = new BootRefusalEvidence("d1", "consent_seed_unwritable", null, "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "", new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Both_null_expectation_still_attributes() {
        var e = new BootRefusalEvidence("d1", "consent_seed_unwritable", null, "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", null, new HashSet<int> { 4242 })).IsTrue();
    }

    [Test]
    public async Task Both_empty_expectation_never_attributes() {
        var e = new BootRefusalEvidence("d1", "consent_seed_unwritable", "", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "", new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Genuinely_different_expectation_never_attributes() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://other.example", new HashSet<int> { 4242 })).IsFalse();
    }

    // ── FakeServiceManager-driven: end-to-end pre-clear + collection + attribution ──

    sealed class FakeServiceManager : IVerifyServiceManager {
        public bool Started, Stopped;
        public int? RunningPid = 4242;
        public Action<string>? OnStart;

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            if (Stopped) return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
            if (Started) return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) { }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) {
            OnStart?.Invoke(serviceId);
            Started = true;
            error = null;
            return true;
        }

        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) =>
            Start(serviceId, timeout, out error);

        public bool Stop(string serviceId, TimeSpan timeout, out string? error) {
            Stopped = true;
            error = null;
            return true;
        }
    }

    static async Task<int> Drive(Task<int> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Minimal launchd plist carrying the consent-seed directive and (optionally) a baked
    /// server expectation — mirrors <c>ServiceVerifyStartTests.MinimalPlist</c>. The identity gate
    /// now fails closed on an unresolvable unit server (spec §3.4(b)), so this also bakes
    /// <c>KCAP_URL</c> whenever <paramref name="expectServerUrl"/> is supplied — otherwise the unit
    /// would have no resolvable identity for Phase A to agree with.</summary>
    static string MinimalPlist(string binary, string consentSeedDefault, string? expectServerUrl) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{Id}</string>
          <key>ProgramArguments</key><array>
            <string>{binary}</string>
          </array>
          <key>EnvironmentVariables</key><dict>
            <key>KCAP_CONSENT_SEED_DEFAULT</key><string>{consentSeedDefault}</string>
            {(expectServerUrl is not null ? $"<key>KCAP_URL</key><string>{expectServerUrl}</string><key>KCAP_EXPECT_SERVER_URL</key><string>{expectServerUrl}</string>" : "")}
          </dict>
        </dict>
        </plist>
        """;

    /// <summary>Invoking env for a gated test that needs Phase A's identity check to pass — both
    /// <c>KCAP_PROFILE</c> and <c>KCAP_EXPECT_SERVER_URL</c> are now required (fail-closed).</summary>
    static Func<string, string?> GatedEnvWithIdentity(string expectServerUrl) => k => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
        "KCAP_PROFILE"              => "default",
        "KCAP_EXPECT_SERVER_URL"    => expectServerUrl,
        _                           => null,
    };

    [Test, NotInParallel]
    public async Task Readiness_timeout_with_matching_marker_attributes_exactly_once_and_consumes_it() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        using var capture = ConsoleOutput.StartErrorCapture();
        try {

            // The observed job pid IS this test process's own pid, matching what BootRefusal.TryWrite
            // (the daemon's real writer) stamps onto the marker — no need to fake a pid.
            var manager = new FakeServiceManager { RunningPid = Environment.ProcessId };
            manager.OnStart = id => {
                var stateDir = Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(id));
                Directory.CreateDirectory(stateDir);
                BootRefusal.TryWrite(stateDir,
                    new DaemonConfig { Name = id, ExpectedServerUrl = "https://s.example", ServerUrl = "https://resolved.example", InstanceId = "inst-1" },
                    "server_expectation_mismatch");
            };

            var time = new FakeTimeProvider();

            static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            // Ownership never matches (validated pid always disagrees with the observed job pid), so
            // readiness never settles and the forward budget genuinely rolls back to a timeout.
            var sut = new ServiceVerify(manager, _ => -1, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2),
                readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", "https://s.example"),
                gateEnv: GatedEnvWithIdentity("https://s.example"),
                digestMatches: _ => true);

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);

            var lines = capture.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Count(l => l == "refusal_reason=server_expectation_mismatch")).IsEqualTo(1);
            await Assert.That(BootRefusalReader.TryRead(Id)).IsNull(); // consumed after attribution
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test, NotInParallel]
    public async Task Readiness_timeout_with_hello_never_well_formed_still_observes_pid_via_direct_query() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        using var capture = ConsoleOutput.StartErrorCapture();
        try {

            // Hello NEVER comes back well-formed — exactly the shape of a REFUSING daemon whose
            // control socket never exists. IsReadyAsync's own Query call therefore never runs; the
            // job pid can only be observed via the direct per-iteration Query the readiness loop
            // now also issues when hello never resolves a pid.
            var manager = new FakeServiceManager { RunningPid = Environment.ProcessId };
            manager.OnStart = id => {
                var stateDir = Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(id));
                Directory.CreateDirectory(stateDir);
                BootRefusal.TryWrite(stateDir,
                    new DaemonConfig { Name = id, ExpectedServerUrl = "https://s.example", ServerUrl = "https://resolved.example", InstanceId = "inst-1" },
                    "server_expectation_mismatch");
            };

            var time = new FakeTimeProvider();

            static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(false, null, null, null)); // never well-formed

            var sut = new ServiceVerify(manager, _ => -1, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2),
                readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", "https://s.example"),
                gateEnv: GatedEnvWithIdentity("https://s.example"),
                digestMatches: _ => true);

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);

            var lines = capture.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Count(l => l == "refusal_reason=server_expectation_mismatch")).IsEqualTo(1);
            await Assert.That(BootRefusalReader.TryRead(Id)).IsNull(); // consumed after attribution
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test, NotInParallel]
    public async Task Preclear_failure_disables_attribution_but_the_mutation_still_proceeds() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        using var capture = ConsoleOutput.StartErrorCapture();
        try {

            // A directory sitting AT the marker path can never be removed via File.Delete — the
            // verified pre-clear must fail, log its notice, and disable coded attribution without
            // blocking the transaction itself.
            Directory.CreateDirectory(BootRefusalReader.MarkerPath(Id));

            var manager = new FakeServiceManager { RunningPid = 4242 };

            static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", "https://s.example"),
                gateEnv: GatedEnvWithIdentity("https://s.example"),
                digestMatches: _ => true);

            var exit = await sut.StartVerifiedAsync(Id);

            // Ownership matches immediately (same pid throughout) — the mutation proceeds to a normal
            // verified success despite the marker never having been cleared.
            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);

            var lines = capture.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Any(l => l == "boot-refusal marker could not be cleared; coded attribution disabled")).IsTrue();
            await Assert.That(lines.Any(l => l.StartsWith("refusal_reason=", StringComparison.Ordinal))).IsFalse();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
