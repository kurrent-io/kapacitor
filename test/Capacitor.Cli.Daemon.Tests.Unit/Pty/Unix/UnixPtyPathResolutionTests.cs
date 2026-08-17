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
        using var tmp = new TempDir();
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("sub/tool", tmp.Path, EnvWithPath(null));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(tmp.PathTo("sub/tool")));
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
        using var tmp = new TempDir();
        var tool = tmp.PathTo("mytool");
        WriteExecutable(tool);
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("mytool", "/no/such/cwd", EnvWithPath(tmp.Path));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Non_executable_earlier_on_path_is_skipped_for_executable_later() {
        if (OperatingSystem.IsWindows()) return;
        // execvp selects the first EXECUTABLE file, not the first that merely EXISTS. A
        // non-executable match earlier on PATH must be skipped in favor of an executable one later,
        // so we preflight the SAME inode the child would exec.
        using var tmp = new TempDir();
        var earlier = tmp.CreateDir("a");
        var later   = tmp.CreateDir("b");
        var shadow  = Path.Combine(earlier, "tool");
        var real    = Path.Combine(later, "tool");
        File.WriteAllText(shadow, ""); // exists but NOT executable (mode 0644-ish)
        File.SetUnixFileMode(shadow, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        WriteExecutable(real);
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("tool", "/no/such/cwd", EnvWithPath($"{earlier}:{later}"));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(real));
    }

    [Test]
    public async Task Not_executable_only_match_throws() {
        if (OperatingSystem.IsWindows()) return;
        // A file that exists but is not executable is invisible to execvp — if it's the ONLY match,
        // resolution fails rather than handing back a non-executable path.
        using var tmp = new TempDir();
        var tool = tmp.PathTo("notexec");
        File.WriteAllText(tool, "");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var threw = false;
        try { UnixPtyProcess.ResolveExecutableAbsolutePath("notexec", "/no/such/cwd", EnvWithPath(tmp.Path)); }
        catch (InvalidOperationException) { threw = true; }
        await Assert.That(threw).IsTrue();
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

        using var tmp = new TempDir();
        var earlier = tmp.CreateDir("a");
        var later   = tmp.CreateDir("b");
        var wrongClass = Path.Combine(earlier, "tool");
        var runnable   = Path.Combine(later, "tool");
        File.WriteAllText(wrongClass, "");
        File.SetUnixFileMode(wrongClass, UnixFileMode.GroupExecute); // 0010: has an exec bit, but NOT for the owner
        WriteExecutable(runnable);
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("tool", "/no/such/cwd", EnvWithPath($"{earlier}:{later}"));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(runnable));
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Empty_path_field_resolves_against_cwd_not_dropped() {
        if (OperatingSystem.IsWindows()) return;
        // A command living ONLY in cwd must be found via an EMPTY PATH field (POSIX cwd) — the
        // case the old RemoveEmptyEntries split silently discarded.
        using var tmp = new TempDir();
        var tool = tmp.PathTo("cwdtool");
        WriteExecutable(tool);
        // Leading empty field (":/definitely/not/here") — the empty field IS cwd, and cwd wins.
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("cwdtool", tmp.Path, EnvWithPath(":/definitely/not/here"));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Relative_path_field_resolves_against_cwd() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var binDir = tmp.CreateDir("bin");
        var tool = Path.Combine(binDir, "reltool");
        WriteExecutable(tool);
        // Relative PATH element "bin" resolves against cwd (not the daemon's own cwd).
        var r = UnixPtyProcess.ResolveExecutableAbsolutePath("reltool", tmp.Path, EnvWithPath("bin"));
        await Assert.That(r).IsEqualTo(Path.GetFullPath(tool));
    }

    [Test]
    public async Task Not_found_anywhere_throws() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var threw = false;
        try { UnixPtyProcess.ResolveExecutableAbsolutePath("nope-" + Guid.NewGuid().ToString("N")[..8], tmp.Path, EnvWithPath("/no/such/dir")); }
        catch (InvalidOperationException) { threw = true; }
        await Assert.That(threw).IsTrue();
    }
}
