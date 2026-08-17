using System.Diagnostics;
using System.Globalization;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Phase B: a real, isolated child process a test fully owns — the ONLY thing the D4 reap
/// tests ever signal (spec §2/§8: no live daemon, no live flows). Sleeps for a while so the test can
/// probe/kill it, and can carry custom <c>KCAP_*</c> env markers for the env-based reap paths.
/// </summary>
public sealed class DummyProcess : IDisposable {
    readonly Process _proc;

    DummyProcess(Process proc) => _proc = proc;

    public int  Pid       => _proc.Id;
    public bool HasExited => _proc.HasExited;

    public static DummyProcess StartSleep(int seconds, IDictionary<string, string>? env = null) {
        // Windows: NOT `timeout /t` — it fails immediately ("ERROR: Input redirection is not
        // supported") whenever stdin isn't a console (headless CI, redirected/closed stdin under
        // parallel test execution), so the "sleep" process exits at once and any liveness-dependent
        // test sees it already dead. `ping -n {N+1} 127.0.0.1` has no stdin dependency and waits ~1s
        // between echoes (≈ N seconds), so the dummy reliably stays alive on a CI runner.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c ping -n {seconds + 1} 127.0.0.1 >NUL")
            : new ProcessStartInfo("sleep", seconds.ToString(CultureInfo.InvariantCulture));

        psi.UseShellExecute = false;

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        return new DummyProcess(Process.Start(psi) ?? throw new InvalidOperationException("failed to start dummy process"));
    }

    public void Kill() {
        try { _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
    }

    public bool WaitForExit(TimeSpan timeout) => _proc.WaitForExit((int) timeout.TotalMilliseconds);
    public void WaitForExit()                 => _proc.WaitForExit();

    public void Dispose() {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        _proc.Dispose();
    }
}
