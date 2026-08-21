namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Covers the shared PATH/PATHEXT resolver behind the headless runners
/// (<c>ClaudeCliRunner</c> / <c>CodexCliRunner</c>) and the daemon's vendor probe.
///
/// <para>The Windows <c>.cmd</c> cases are the regression this type exists for:
/// <c>ProcessStartInfo.FileName</c> with <c>UseShellExecute = false</c> goes through
/// <c>CreateProcess</c>, which appends only <c>.exe</c>. npm installs the agent CLIs as a
/// <c>.cmd</c> shim with no <c>.exe</c>, so passing a bare <c>"codex"</c>/<c>"claude"</c>
/// failed with "The system cannot find the file specified" — silently degrading session
/// titles and what's-done summaries on Windows for every npm-installed harness.</para>
/// </summary>
public class CliExecutableTests {
    [Test]
    public async Task Resolve_returns_null_for_empty_input() {
        await Assert.That(CliExecutable.Resolve(null)).IsNull();
        await Assert.That(CliExecutable.Resolve("")).IsNull();
        await Assert.That(CliExecutable.Resolve("   ")).IsNull();
    }

    [Test]
    public async Task Resolve_returns_null_when_bare_command_not_on_path() {
        await Assert.That(CliExecutable.Resolve($"kcap-absent-{Guid.NewGuid():N}")).IsNull();
    }

    /// <summary>The whole point: a bare command backed only by a <c>.cmd</c> shim must resolve
    /// to that shim's full path, because <c>CreateProcess</c> will not find it otherwise.</summary>
    [Test, NotInParallel]
    public async Task Resolve_finds_cmd_shim_for_bare_command_on_windows() {
        if (!OperatingSystem.IsWindows()) return; // .cmd shims are a Windows-only npm artifact

        using var tmp = new TempDir();
        var name = $"kcap-shimprobe-{Guid.NewGuid():N}";
        var shim = tmp.PathTo(name + ".cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\necho hi\r\n");

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        try {
            var resolved = CliExecutable.Resolve(name);

            // Case-insensitive: the extension comes from PATHEXT, which is conventionally
            // uppercase (".CMD"), so the resolved path won't match the on-disk casing. Harmless
            // — Windows paths are case-insensitive and CreateProcess accepts either.
            await Assert.That(resolved).IsEqualTo(shim, StringComparison.OrdinalIgnoreCase);
            await Assert.That(CliExecutable.Exists(name)).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    /// <summary>The CreateProcessW-193 regression: npm installs an extensionless Git-Bash
    /// <c>#!/bin/sh</c> shim RIGHT NEXT TO <c>codex.cmd</c>. A bare extensionless file is not
    /// launchable via <c>CreateProcess</c> (error 193 — not a valid Win32 application), so with
    /// both present the resolver must return the <c>.cmd</c>, never the shim.</summary>
    [Test, NotInParallel]
    public async Task Resolve_prefers_cmd_over_extensionless_twin_on_windows() {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var name = $"kcap-twinprobe-{Guid.NewGuid():N}";
        var shim = tmp.PathTo(name);          // extensionless "#!/bin/sh" shim
        var cmd  = tmp.PathTo(name + ".cmd");
        await File.WriteAllTextAsync(shim, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(cmd, "@echo off\r\n");

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        try {
            await Assert.That(CliExecutable.Resolve(name)).IsEqualTo(cmd, StringComparison.OrdinalIgnoreCase);
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    /// <summary>Same twin, reached through a rooted extensionless configured path
    /// (<c>daemon.codex_path = C:\tools\codex</c>): must still land on <c>codex.cmd</c>, not the
    /// extensionless shim beside it.</summary>
    [Test]
    public async Task Resolve_prefers_cmd_over_extensionless_twin_for_rooted_path_on_windows() {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var stem = tmp.PathTo("codex");
        await File.WriteAllTextAsync(stem, "#!/bin/sh\n");        // extensionless shim
        await File.WriteAllTextAsync(stem + ".cmd", "@echo off\r\n");

        await Assert.That(CliExecutable.Resolve(stem))
            .IsEqualTo(stem + ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A configured <c>daemon.codex_path</c> may omit the extension; on Windows that
    /// must still land on the <c>.cmd</c> next to it rather than reporting "not installed".</summary>
    [Test]
    public async Task Resolve_appends_extension_to_rooted_path_on_windows() {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var stem = tmp.PathTo("codex");
        await File.WriteAllTextAsync(stem + ".cmd", "@echo off\r\n");

        await Assert.That(CliExecutable.Resolve(stem))
            .IsEqualTo(stem + ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Resolve_returns_fully_qualified_path_for_relative_input() {
        using var tmp = new TempDir();
        var name = OperatingSystem.IsWindows() ? "probe.cmd" : "probe";
        var full = tmp.PathTo(name);
        await File.WriteAllTextAsync(full, "#!/bin/sh\nexit 0\n");
        MakeExecutable(full);

        // A path with a directory component but no root — must resolve against the cwd, not PATH.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), full);

        var resolved = CliExecutable.Resolve(relative);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(Path.IsPathFullyQualified(resolved!)).IsTrue();
        await Assert.That(resolved).IsEqualTo(full);
    }

    [Test]
    public async Task Resolve_returns_null_for_non_executable_file_on_unix() {
        if (OperatingSystem.IsWindows()) return; // Windows has no exec bit

        using var tmp = new TempDir();
        var path = tmp.PathTo("probe");
        await File.WriteAllTextAsync(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);

        await Assert.That(CliExecutable.Resolve(path)).IsNull();
    }

    /// <summary>An unusable PATH entry must not abort the walk before later entries are tried —
    /// a missing directory and a stray-quote entry both have to be skipped, not fatal.</summary>
    [Test, NotInParallel]
    public async Task Resolve_continues_past_unusable_path_entries() {
        using var tmp = new TempDir();
        var name = $"kcap-lateprobe-{Guid.NewGuid():N}";
        var file = tmp.PathTo(OperatingSystem.IsWindows() ? name + ".cmd" : name);
        await File.WriteAllTextAsync(file, "");
        MakeExecutable(file);

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var junk      = $"{tmp.PathTo("nope")}{Path.PathSeparator}\"quoted{Path.PathSeparator}";
        // Junk entries FIRST so the good directory is only reached if the walk continued.
        Environment.SetEnvironmentVariable("PATH", junk + tmp.Path);

        try {
            await Assert.That(CliExecutable.Resolve(name)).IsEqualTo(file, StringComparison.OrdinalIgnoreCase);
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    static void MakeExecutable(string path) {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
          | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
          | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        );
    }
}
