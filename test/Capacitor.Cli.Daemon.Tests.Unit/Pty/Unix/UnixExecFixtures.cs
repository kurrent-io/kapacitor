using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// On-disk executables for the L1-shim tests: shebang scripts and permission-bit variants of a real
/// binary, each built inside a <see cref="TempDir"/> the calling test owns.
///
/// <para>Lives here rather than in Helpers because only the Unix Pty suites need it, and the
/// permission bits it sets are meaningless off Unix — every caller is <c>[RunOn]</c>-gated.</para>
/// </summary>
static partial class UnixExecFixtures {
    /// <summary>A real shebang script `#!/abs/interp [optarg]\n&lt;body&gt;` that just `exit`s, chmod +x —
    /// a native no-op binary is not enough to exercise the shebang branches.</summary>
    public static string ShebangScript(
            this TempDir dir, string name, string interpAbsPath, string? optArg, string body) {
        var shebang = optArg is null ? $"#!{interpAbsPath}\n" : $"#!{interpAbsPath} {optArg}\n";
        var path    = dir.CreateFile(name, shebang + body);

        MakeExecutable(path);

        return path;
    }

    /// <summary>A copy of a real binary that is executable but NOT readable (chmod 0111) —
    /// exercises the "EXEC_PATH plans need no readable fd" §5 case.</summary>
    public static string ExecuteOnlyCopyOf(this TempDir dir, string sourceAbsPath) =>
        CopyWithMode(dir.PathTo("exec-only"), sourceAbsPath, 0b001_001_001 /* 0111 */);

    /// <summary>A copy of a real binary with the setuid bit set — never actually exec'd (privileged
    /// preflight must classify it uncontained and the test never runs it as a real setuid binary,
    /// avoiding any real privilege escalation risk in CI).</summary>
    public static string SetuidCopyOf(this TempDir dir, string sourceAbsPath) =>
        CopyWithMode(dir.PathTo("setuid-copy"), sourceAbsPath, 0b100_111_101_101 /* 04755 */);

    /// <summary>A directory containing a single real, non-privileged executable named
    /// <paramref name="name"/> (a +x copy of /bin/true) — so an ABSOLUTE PATH component actually
    /// resolves the target. Used to prove that an empty/relative SIBLING element (not a missing
    /// target) is what forces uncontained; without a resolvable absolute component the test would
    /// pass on `!resolved` alone and never exercise the empty-field detection.</summary>
    public static string DirWithExecutable(this TempDir dir, string name) =>
        DirWithExecutableCopy(dir, "path-with-target", name, "/bin/true");

    /// <summary>Two directories, each containing an executable named <paramref name="name"/> that
    /// behaves differently (one copies /bin/true, the other /bin/false) — for asserting which PATH a
    /// resolution actually used.</summary>
    public static (string daemonDir, string childDir) TwoDirsWithDifferentExecutable(
            this TempDir dir, string name) =>
        (DirWithExecutableCopy(dir, "daemon-path", name, "/bin/true"),
         DirWithExecutableCopy(dir, "child-path",  name, "/bin/false"));

    /// <summary>True when the effective user is root — access(X_OK) bypasses permission-class checks
    /// for root (it succeeds if ANY execute bit is set), so tests that rely on a wrong-class execute
    /// bit being REJECTED must skip under root to stay meaningful.</summary>
    public static bool IsEffectiveRoot() => !OperatingSystem.IsWindows() && geteuid_native() == 0;

    public static void MakeExecutable(string path) => Chmod(path, 0b111_101_101 /* 0755 */);

    static string DirWithExecutableCopy(TempDir dir, string subdir, string name, string sourceAbsPath) {
        var created = dir.CreateDir(subdir);
        var target  = created.PathTo(name);

        File.Copy(sourceAbsPath, target);
        MakeExecutable(target);

        return created.Path;
    }

    static string CopyWithMode(string path, string sourceAbsPath, int mode) {
        File.Copy(sourceAbsPath, path, overwrite: true);
        Chmod(path, mode);

        return path;
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial uint geteuid_native();

    [LibraryImport("libc", EntryPoint = "chmod", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int chmod_native(string path, int mode);

    static void Chmod(string path, int mode) {
        if (chmod_native(path, mode) != 0)
            throw new InvalidOperationException($"chmod {Convert.ToString(mode, 8)} {path} failed: errno {Marshal.GetLastPInvokeError()}");
    }
}
