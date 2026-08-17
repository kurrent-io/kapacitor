using System.Runtime.Versioning;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// Q1: <see cref="UnixPtyProcess.ResolveExecutableAbsolutePath"/> must mirror the child's own
/// exec-time PATH resolution — the native child does <c>chdir(cwd)</c> before exec, so an EMPTY
/// PATH field (POSIX current directory) and any RELATIVE field resolve against <c>cwd</c>, not
/// the daemon's cwd, and must NOT be silently dropped (the old <c>RemoveEmptyEntries</c> split
/// discarded exactly those fields, so a command living only in <c>cwd</c> went unfound here while
/// the child would have exec'd it fine). POSIX-PATH semantics, so gated off Windows (which never
/// uses this Unix resolver).
/// </summary>
public class UnixPtyPathResolutionTests {
    static IReadOnlyDictionary<string, string> EnvWithPath(string? path) {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is not null) d["PATH"] = path;
        return d;
    }

    [Test]
    public async Task Absolute_command_is_returned_as_is() {
        if (OperatingSystem.IsWindows()) return;
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("/usr/bin/tool", "/some/cwd", EnvWithPath(null));
        await Assert.That(r).IsEqualTo("/usr/bin/tool");
    }

    [Test]
    public async Task Slashed_relative_command_resolves_against_cwd() {
        if (OperatingSystem.IsWindows()) return;
        var cwd = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("sub/tool", cwd, EnvWithPath(null));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(Path.Combine(cwd, "sub/tool")));
        } finally { Directory.Delete(cwd, true); }
    }

    // Create an EXECUTABLE regular file (mode rwx------) — the resolver now honors the execute bit
    // like execvp, so PATH-fixture tools must actually be executable to be selected.
    [UnsupportedOSPlatform("windows")]
    static void WriteExecutable(string path) {
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Bare_command_found_in_an_absolute_path_dir() {
        if (OperatingSystem.IsWindows()) return;
        var dir  = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var tool = Path.Combine(dir, "mytool");
        WriteExecutable(tool);
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("mytool", "/no/such/cwd", EnvWithPath(dir));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Non_executable_earlier_on_path_is_skipped_for_executable_later() {
        if (OperatingSystem.IsWindows()) return;
        // execvp selects the first EXECUTABLE file, not the first that merely EXISTS. A
        // non-executable match earlier on PATH must be skipped in favor of an executable one later,
        // so we preflight the SAME inode the child would exec.
        var earlier = Directory.CreateTempSubdirectory("kcap-resolve-a-").FullName;
        var later   = Directory.CreateTempSubdirectory("kcap-resolve-b-").FullName;
        var shadow  = Path.Combine(earlier, "tool");
        var real    = Path.Combine(later, "tool");
        File.WriteAllText(shadow, ""); // exists but NOT executable (mode 0644-ish)
        File.SetUnixFileMode(shadow, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        WriteExecutable(real);
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("tool", "/no/such/cwd", EnvWithPath($"{earlier}:{later}"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(real));
        } finally { Directory.Delete(earlier, true); Directory.Delete(later, true); }
    }

    [Test]
    public async Task Not_executable_only_match_throws() {
        if (OperatingSystem.IsWindows()) return;
        // A file that exists but is not executable is invisible to execvp — if it's the ONLY match,
        // resolution fails rather than handing back a non-executable path.
        var dir  = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var tool = Path.Combine(dir, "notexec");
        File.WriteAllText(tool, "");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try {
            var threw = false;
            try { UnixPtyProcess.ResolveExecutableAbsolutePath("notexec", "/no/such/cwd", EnvWithPath(dir)); }
            catch (InvalidOperationException) { threw = true; }
            await Assert.That(threw).IsTrue();
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Exec_bit_wrong_permission_class_is_skipped_like_access_x_ok() {
        if (OperatingSystem.IsWindows()) return;
        // execvp uses access(X_OK) — permission-CLASS-aware — not "any execute bit set". A file the
        // owner can't execute (mode 0010: group-execute only) must be SKIPPED even though it carries
        // an execute bit, so the resolver falls through to a genuinely runnable candidate later on
        // PATH, exactly as execvp would. Under root access(X_OK) bypasses the class check, so skip.
        if (UnixExecFixtures.IsEffectiveRoot()) return;

        var earlier = Directory.CreateTempSubdirectory("kcap-resolve-a-").FullName;
        var later   = Directory.CreateTempSubdirectory("kcap-resolve-b-").FullName;
        var wrongClass = Path.Combine(earlier, "tool");
        var runnable   = Path.Combine(later, "tool");
        File.WriteAllText(wrongClass, "");
        File.SetUnixFileMode(wrongClass, UnixFileMode.GroupExecute); // 0010: has an exec bit, but NOT for the owner
        WriteExecutable(runnable);
        try {
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("tool", "/no/such/cwd", EnvWithPath($"{earlier}:{later}"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(runnable));
        } finally { Directory.Delete(earlier, true); Directory.Delete(later, true); }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Empty_path_field_resolves_against_cwd_not_dropped() {
        if (OperatingSystem.IsWindows()) return;
        // A command living ONLY in cwd must be found via an EMPTY PATH field (POSIX cwd) — the
        // case the old RemoveEmptyEntries split silently discarded.
        var cwd  = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var tool = Path.Combine(cwd, "cwdtool");
        WriteExecutable(tool);
        try {
            // Leading empty field (":/definitely/not/here") — the empty field IS cwd, and cwd wins.
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("cwdtool", cwd, EnvWithPath(":/definitely/not/here"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(cwd, true); }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Relative_path_field_resolves_against_cwd() {
        if (OperatingSystem.IsWindows()) return;
        var cwd    = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        var binDir = Path.Combine(cwd, "bin");
        Directory.CreateDirectory(binDir);
        var tool = Path.Combine(binDir, "reltool");
        WriteExecutable(tool);
        try {
            // Relative PATH element "bin" resolves against cwd (not the daemon's own cwd).
            var r = UnixPtyProcess.ResolveExecutableAbsolutePath("reltool", cwd, EnvWithPath("bin"));
            await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
        } finally { Directory.Delete(cwd, true); }
    }

    [Test]
    public async Task Not_found_anywhere_throws() {
        if (OperatingSystem.IsWindows()) return;
        var cwd = Directory.CreateTempSubdirectory("kcap-resolve-").FullName;
        try {
            var threw = false;
            try { UnixPtyProcess.ResolveExecutableAbsolutePath("nope-" + Guid.NewGuid().ToString("N")[..8], cwd, EnvWithPath("/no/such/dir")); }
            catch (InvalidOperationException) { threw = true; }
            await Assert.That(threw).IsTrue();
        } finally { Directory.Delete(cwd, true); }
    }
}
