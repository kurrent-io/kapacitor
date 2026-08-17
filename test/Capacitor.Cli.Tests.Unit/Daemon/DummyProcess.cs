using System.Diagnostics;
using System.Globalization;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Phase B: a real, isolated child process a test fully owns — the ONLY thing the D4 reap
/// tests ever signal (spec §2/§8: no live daemon, no live flows). Sleeps for a while so the test can
/// probe/kill it, and can carry custom <c>KCAP_*</c> env markers for the env-based reap paths.
/// </summary>
internal sealed partial class DummyProcess : IDisposable {
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

    /// <summary>Writes a native no-op ELF-less "script" is not enough for the shebang tests — this
    /// writes a real shebang script `#!/abs/interp [optarg]\n&lt;body&gt;` that just `exit`s, chmod +x.</summary>
    public static string WriteShebangScript(string interpAbsPath, string? optArg, string body) {
        var path = Path.Combine(Path.GetTempPath(), "kcap-shim-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
        var shebang = optArg is null ? $"#!{interpAbsPath}\n" : $"#!{interpAbsPath} {optArg}\n";
        File.WriteAllText(path, shebang + body);
        MakeExecutable(path);
        return path;
    }

    /// <summary>A native executable that's readable but chmod 0111 (execute-only, no read bit) —
    /// exercises the "EXEC_PATH plans need no readable fd" §5 case.</summary>
    public static string CopyExecuteOnly(string sourceAbsPath) {
        var path = Path.Combine(Path.GetTempPath(), "kcap-shim-x-" + Guid.NewGuid().ToString("N")[..8]);
        File.Copy(sourceAbsPath, path, overwrite: true);
        Chmod(path, 0b001_001_001); // 0111
        return path;
    }

    /// <summary>A copy of a real binary with the setuid bit set — never actually exec'd (privileged
    /// preflight must classify it uncontained and the test never runs it as a real setuid binary,
    /// avoiding any real privilege escalation risk in CI).</summary>
    public static string CopySetuid(string sourceAbsPath) {
        var path = Path.Combine(Path.GetTempPath(), "kcap-shim-suid-" + Guid.NewGuid().ToString("N")[..8]);
        File.Copy(sourceAbsPath, path, overwrite: true);
        Chmod(path, 0b100_111_101_101 /* 04755 */);
        return path;
    }

    /// <summary>Two temp directories, each containing an executable named <paramref name="name"/>
    /// that behaves differently (one is a copy of /bin/true, the other /bin/false) — for asserting
    /// which PATH a resolution actually used.</summary>
    public static (string daemonDir, string childDir) TwoDistinctPathDirsWithDifferentTarget(string name) {
        var daemonDir = Directory.CreateTempSubdirectory("kcap-daemon-path-").FullName;
        var childDir  = Directory.CreateTempSubdirectory("kcap-child-path-").FullName;
        File.Copy("/bin/true",  Path.Combine(daemonDir, name));
        File.Copy("/bin/false", Path.Combine(childDir, name));
        MakeExecutable(Path.Combine(daemonDir, name));
        MakeExecutable(Path.Combine(childDir, name));
        return (daemonDir, childDir);
    }

    /// <summary>A temp directory containing a single real, non-privileged executable named
    /// <paramref name="name"/> (a +x copy of /bin/true) — so an ABSOLUTE PATH component actually
    /// resolves the target. Used to prove that an empty/relative SIBLING element (not a missing
    /// target) is what forces uncontained; without a resolvable absolute component the test would
    /// pass on `!resolved` alone and never exercise the empty-field detection.</summary>
    public static string PathDirWithTarget(string name) {
        var dir    = Directory.CreateTempSubdirectory("kcap-path-").FullName;
        var target = Path.Combine(dir, name);
        File.Copy("/bin/true", target);
        MakeExecutable(target);
        return dir;
    }

    public static void MakeExecutablePublic(string path) => MakeExecutable(path);

    static void MakeExecutable(string path) => Chmod(path, 0b111_101_101 /* 0755 */);

    /// <summary>True when the effective user is root — access(X_OK) bypasses permission-class checks
    /// for root (it succeeds if ANY execute bit is set), so tests that rely on a wrong-class execute
    /// bit being REJECTED must skip under root to stay meaningful.</summary>
    public static bool IsEffectiveRoot() => !OperatingSystem.IsWindows() && geteuid_native() == 0;

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "geteuid")]
    [System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial uint geteuid_native();

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "chmod", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    [System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int chmod_native(string path, int mode);

    static void Chmod(string path, int mode) {
        if (chmod_native(path, mode) != 0)
            throw new InvalidOperationException($"chmod {Convert.ToString(mode, 8)} {path} failed: errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
    }
}
