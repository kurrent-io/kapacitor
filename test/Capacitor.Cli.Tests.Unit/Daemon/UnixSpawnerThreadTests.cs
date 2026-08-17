using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// L1-managed(a) (spec §4.2(b)): every Linux pty_spawn call runs on ONE dedicated,
/// daemon-lifetime native thread — never a pool thread (PR_SET_PDEATHSIG is a per-THREAD
/// property; a pool thread retiring would SIGKILL every agent it spawned). Two of these three
/// tests need a real separate OS process (see this task's testability note) via the
/// NativeTestHost helper; the third runs in-process.
/// </summary>
public class UnixSpawnerThreadTests {
    [Test]
    public async Task Pdeathsig_kills_the_child_when_the_spawner_process_dies() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = StartHost("spawn-dummy");
        var childPid = await ReadPidLineAsync(host);

        host.Kill(entireProcessTree: false); // simulate an external daemon crash (SIGKILL)
        host.WaitForExit(5000);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsAlive(childPid)) await Task.Delay(200);

        await Assert.That(IsAlive(childPid)).IsFalse();
    }

    [Test]
    public async Task Unexpected_spawner_thread_exit_fail_fasts_the_host_process() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = StartHost("crash-spawner");
        var exited = host.WaitForExit(10000);

        await Assert.That(exited).IsTrue();
        // Environment.FailFast raises SIGABRT on Unix — .NET reports that as a large/negative
        // native exit code (128 + signal, i.e. 134), NOT a clean 0. Assert it's non-zero rather
        // than pin the exact platform-dependent encoding.
        await Assert.That(host.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task Agent_survives_unrelated_pool_thread_churn_while_the_thread_lives() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();

        // Churn several short-lived pool threads BEFORE and AFTER the spawn — none of them is
        // the thread that called pty_spawn, so none of their deaths should matter.
        for (var i = 0; i < 5; i++) await Task.Run(() => { });

        // envp must be a NULL-terminated array (mirrors execve/PtySpawnTests' EmptyEnvp()) —
        // pty_preflight/pty_spawn walk it as `char *const envp[]` until a NULL sentinel, so a
        // genuinely zero-length managed array (no trailing null) would read past the end of
        // whatever the marshaller allocated. Probe execveat support rather than hardcoding 1
        // (mirrors PtySpawnTests.Preflight): macOS always reports 0, and forcing an EXEC_FD plan
        // there fails at the exec step (pty_spawn has no fd-exec primitive off Linux).
        var execveatSupported = UnixPtyInterop.pty_probe_execveat();
        var rc = UnixPtyInterop.pty_preflight("/bin/sleep", ["sleep", "3", null], [null], execveatSupported, out var plan);
        await Assert.That(rc).IsEqualTo(0);

        var result = spawner.SpawnOn(plan, [null], Directory.GetCurrentDirectory(), 40, 120, Environment.ProcessId, -1);
        try {
            await Assert.That(result.Pid).IsGreaterThan(0);
            for (var i = 0; i < 5; i++) await Task.Run(() => { });
            await Task.Delay(300);
            await Assert.That(IsAlive(result.Pid)).IsTrue();
        } finally {
            // Guard against pid<=0: kill(0, sig)/kill(-1, sig) have special "whole process
            // group"/"every process" meanings on Unix — never pass through a failed spawn's
            // Pid=0 (a real bug caught while writing this test: it SIGKILLed this entire test
            // host's process group instead of a single child).
            if (result.Pid > 0) {
                UnixPtyInterop.kill(result.Pid, UnixPtyInterop.SIGKILL);
                UnixPtyInterop.waitpid(result.Pid, out _, 0);
            }
            var p = plan; UnixPtyInterop.pty_plan_free(ref p);
        }
    }

    [Test]
    public async Task Dispose_joins_the_spawner_thread_before_returning() {
        // Regression for the dispose race (Q2): Dispose must not return — and must not dispose
        // the BlockingCollection — until the loop thread has actually exited, otherwise it could
        // free the queue out from under a still-running GetConsumingEnumerable(). With no spawn
        // in flight the CompleteAdding()+Join() completes near-instantly; the assertion is that
        // the thread is provably gone once Dispose returns. Pure managed lifecycle (the loop only
        // parks on the empty queue — no pty_spawn, no shim), so it runs on any platform.
        var spawner = new UnixSpawnerThread();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        spawner.Dispose();

        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    static bool IsAlive(int pid) => UnixPtyInterop.kill(pid, 0) == 0;

    static Process StartHost(string mode) {
        var dll = ResolveNativeHostDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {mode}") {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start NativeTestHost");
    }

    static async Task<int> ReadPidLineAsync(Process host) {
        var line = await host.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("no PID line from host");
        return int.Parse(line["PID=".Length..], CultureInfo.InvariantCulture);
    }

    // Sibling-project resolution: the test assembly and the host live at
    // test/Capacitor.Cli.Tests.Unit/bin/<Config>/<TFM>/ and
    // test/Capacitor.Cli.Tests.Unit.NativeTestHost/bin/<Config>/<TFM>/ respectively. Adjust this
    // if your local build layout differs (e.g. a custom -o/OutDir); the value is deliberately
    // derived rather than a hardcoded absolute path so CI and local dev share the same logic.
    static string ResolveNativeHostDll() {
        var dir       = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm       = Path.GetFileName(dir);
        var config    = Path.GetFileName(Path.GetDirectoryName(dir)!);
        var testRoot  = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var hostDll   = Path.Combine(testRoot, "Capacitor.Cli.Tests.Unit.NativeTestHost", "bin", config, tfm,
            "Capacitor.Cli.Tests.Unit.NativeTestHost.dll");

        if (!File.Exists(hostDll))
            throw new InvalidOperationException($"NativeTestHost not built at {hostDll} — build Capacitor.slnx first");

        return hostDll;
    }
}
