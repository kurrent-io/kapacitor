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

    static (string Home, string PlistPath) SetUpHome(string id) {
        var home = Directory.CreateTempSubdirectory("kcap-startstop-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);
        var dir = LaunchdUnit.AgentsDir();
        Directory.CreateDirectory(dir);
        var path = LaunchdUnit.PlistPath(id);
        File.WriteAllText(path, "<plist/>");
        return (home, path);
    }

    static async Task WithHome(Func<string, Task> body) {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var id = "test";
        var (home, path) = SetUpHome(id);
        try {
            await body(path);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { Directory.Delete(home, recursive: true); } catch { /* best-effort */ }
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
