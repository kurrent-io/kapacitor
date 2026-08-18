using System.Runtime.InteropServices;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Spec §3.4: <c>Stop</c> unloads the label via <c>bootout</c> — a SIGTERM cannot stop a
/// lock-losing <c>KeepAlive</c> job caught between short-lived incarnations — and the plist is
/// retained (stopping is not uninstalling). <c>Start</c> probes the label first: <c>bootstrap</c>
/// the plist when unloaded, <c>kickstart</c> when already loaded, and fails without issuing
/// either launchctl mutation when the probe is ambiguous.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public partial class LaunchdStartStopTests {
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static int Uid() => (int)getuid();

    static string SetUpHome(string id, string home) {
        Environment.SetEnvironmentVariable("HOME", home);
        var dir = LaunchdUnit.AgentsDir();
        Directory.CreateDirectory(dir);
        var path = LaunchdUnit.PlistPath(id);
        File.WriteAllText(path, "<plist/>");
        return path;
    }

    static async Task WithHome(Func<string, Task> body) {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var id = "test";
        using var tmp = new TempDir();
        var path = SetUpHome(id, tmp.Path);
        try {
            await body(path);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    // ── Stop ──

    [Test]
    public async Task Stop_issues_bootout_not_kill_and_retains_plist() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            List<string[]> calls = [];
            var mgr = new LaunchdServiceManager(runProcess: (_, args) => {
                calls.Add(args);
                return (0, "", "");
            });

            var ok = mgr.Stop("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(calls.Count).IsEqualTo(1);
            await Assert.That(calls[0]).IsEquivalentTo(["bootout", $"gui/{Uid()}/io.kurrent.kcap.daemon.test"]);
        });
    }

    [Test]
    public async Task Stop_bootout_failure_with_benign_absence_on_requery_succeeds_and_retains_plist() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "bootout"
                    ? (113, "", "")
                    : (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501"));

            var ok = mgr.Stop("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(File.Exists(path)).IsTrue();
        });
    }

    [Test]
    public async Task Stop_bootout_failure_with_still_loaded_on_requery_fails_and_retains_plist() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "bootout"
                    ? (1, "", "Operation not permitted")
                    : (0, "state = running\npid = 924\n", ""));

            var ok = mgr.Stop("test", out var error);

            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(File.Exists(path)).IsTrue();
        });
    }

    // ── Start ──

    [Test]
    public async Task Start_probe_absent_issues_bootstrap_with_plist_path() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            List<string[]> calls = [];
            var mgr = new LaunchdServiceManager(runProcess: (_, args) => {
                calls.Add(args);
                return args[0] == "print"
                    ? (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501")
                    : (0, "", "");
            });

            var ok = mgr.Start("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(calls.Count).IsEqualTo(2);
            await Assert.That(calls[0][0]).IsEqualTo("print");
            await Assert.That(calls[1]).IsEquivalentTo(["bootstrap", $"gui/{Uid()}", path]);
        });
    }

    [Test]
    public async Task Start_probe_loaded_issues_kickstart() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            List<string[]> calls = [];
            var mgr = new LaunchdServiceManager(runProcess: (_, args) => {
                calls.Add(args);
                return args[0] == "print"
                    ? (0, "state = running\npid = 924\n", "")
                    : (0, "", "");
            });

            var ok = mgr.Start("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(calls.Count).IsEqualTo(2);
            await Assert.That(calls[0][0]).IsEqualTo("print");
            await Assert.That(calls[1]).IsEquivalentTo(["kickstart", $"gui/{Uid()}/io.kurrent.kcap.daemon.test"]);
        });
    }

    // ── WriteAndBootstrap (install-verify's fresh-install mutation) ──

    [Test]
    public async Task WriteAndBootstrap_writes_the_unit_and_bootstraps_without_a_leading_bootout() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            List<string[]> calls = [];
            var mgr = new LaunchdServiceManager(runProcess: (_, args) => {
                calls.Add(args);
                return (0, "", "");
            });
            var spec = new ServiceSpec("test", "/opt/kcap/kcap-daemon", "/tmp/daemon-test.log",
                new Dictionary<string, string>(), []);

            mgr.WriteAndBootstrap(spec);

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(calls.Count).IsEqualTo(1);
            await Assert.That(calls[0]).IsEquivalentTo(["bootstrap", $"gui/{Uid()}", path]);
        });
    }

    // ── bounded verify-path ops: a hung launchctl (RunBounded reports TimedOut) maps to a bounded
    //    failure rather than blocking, so the transaction's deadline is never overrun. ──

    [Test]
    public async Task Bounded_query_maps_a_timed_out_launchctl_print_to_unknown() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async _ => {
            var mgr = new LaunchdServiceManager(runBounded: (_, _, _) => (137, "", "", true));

            var q = mgr.Query("test", TimeSpan.FromSeconds(1));

            await Assert.That(q.Probe).IsEqualTo(LabelProbe.Unknown);
        });
    }

    [Test]
    public async Task Bounded_uninstall_on_a_timed_out_bootout_retains_the_plist_and_fails() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runBounded: (_, _, _) => (0, "", "", true));

            var ok = mgr.Uninstall("test", TimeSpan.FromSeconds(1), out var error);

            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(File.Exists(path)).IsTrue();
        });
    }

    [Test]
    public async Task Bounded_write_and_bootstrap_throws_on_a_timed_out_bootstrap() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async _ => {
            var mgr = new LaunchdServiceManager(writeUnit: (_, _, _) => { }, runBounded: (_, _, _) => (0, "", "", true));
            var spec = new ServiceSpec("test", "/opt/kcap/kcap-daemon", "/tmp/daemon-test.log", new Dictionary<string, string>(), []);

            await Assert.That(() => mgr.WriteAndBootstrap(spec, TimeSpan.FromSeconds(1))).Throws<TimeoutException>();
        });
    }

    // ── Query containment: a malformed on-disk plist (duplicate key, truncated XML) must never
    // escape Query as an uncoded failure — Query is a total, never-throwing probe. See
    // ServiceVerifyStartGateProductionPathTests for the same shape carried through the gate. ──

    [Test]
    public async Task Query_does_not_throw_on_a_duplicate_ProgramArguments_key_plist_and_reports_null_binary_path() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            const string duplicateKeyPlist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>io.kurrent.kcap.daemon.test</string>
                  <key>ProgramArguments</key><array>
                    <string>/bin/kcap-daemon</string>
                  </array>
                  <key>ProgramArguments</key><array>
                    <string>/bin/evil-daemon</string>
                  </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(path, duplicateKeyPlist);

            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "print"
                    ? (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501")
                    : (0, "", ""));

            var query = mgr.Query("test");

            await Assert.That(query.BinaryPath).IsNull();
            await Assert.That(query.UnitPresent).IsTrue();
            await Assert.That(query.Probe).IsEqualTo(LabelProbe.Absent);
        });
    }

    [Test]
    public async Task Status_does_not_throw_on_a_malformed_plist_and_reports_null_binary_path() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            File.WriteAllText(path, "<plist version=\"1.0\"><dict><key>Truncated");

            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "print"
                    ? (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501")
                    : (0, "", ""));

            var status = mgr.Status("test");

            await Assert.That(status.BinaryPath).IsNull();
        });
    }

    // ── StartBootstrapOnly budget discipline ──

    [Test]
    public async Task StartBootstrapOnly_shares_one_deadline_across_probe_and_bootstrap_not_two_full_budgets() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async _ => {
            var timeouts = new List<TimeSpan>();
            var mgr = new LaunchdServiceManager(runBounded: (_, args, timeout) => {
                timeouts.Add(timeout);
                if (args[0] == "print") {
                    Thread.Sleep(50); // consume a real, measurable slice of the shared budget
                    return (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501", false);
                }
                return (0, "", "", false);
            });

            var ok = mgr.StartBootstrapOnly("test", TimeSpan.FromSeconds(5), out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(timeouts.Count).IsEqualTo(2);
            // The probe gets the full budget; the bootstrap call must get only what's LEFT of it —
            // never another full 5s, which would let the pair invade up to 2x the caller's forward
            // remainder (including the separately reserved rollback budget).
            await Assert.That(timeouts[0]).IsEqualTo(TimeSpan.FromSeconds(5));
            await Assert.That(timeouts[1]).IsLessThan(TimeSpan.FromSeconds(5));
        });
    }

    [Test]
    public async Task StartBootstrapOnly_reports_timeout_when_the_probe_alone_exhausts_the_budget() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async _ => {
            var mgr = new LaunchdServiceManager(runBounded: (_, args, timeout) =>
                args[0] == "print"
                    ? (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501", true) // timed out
                    : (0, "", "", false));

            var ok = mgr.StartBootstrapOnly("test", TimeSpan.FromSeconds(5), out var error);

            // A timed-out print reports Unknown, not Absent — bootstrap must never run.
            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
        });
    }

    [Test]
    public async Task Start_probe_unknown_fails_without_issuing_bootstrap_or_kickstart() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            List<string[]> calls = [];
            var mgr = new LaunchdServiceManager(runProcess: (_, args) => {
                calls.Add(args);
                return (1, "", "Operation not permitted");
            });

            var ok = mgr.Start("test", out var error);

            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(calls.Count).IsEqualTo(1);
            await Assert.That(calls[0][0]).IsEqualTo("print");
        });
    }
}
