using System.Runtime.Versioning;
using Capacitor.Cli.Core.Setup;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// <summary>
/// The search-path walk and the resolution behind it. Launch rules are the host's, so each OS
/// pins its own half: the <c>.cmd</c> cases are the Windows regression this type exists for —
/// <c>CreateProcess</c> appends only <c>.exe</c>, and npm drops an extensionless <c>#!/bin/sh</c>
/// shim beside <c>codex.cmd</c>, so a bare <c>"codex"</c> either fails to launch or lands on the
/// shim (error 193) and silently degrades session titles.
/// </summary>
public class BinaryProbeTests {
    [Test]
    public async Task Resolve_is_null_for_empty_input() {
        var probe = BinaryProbe.Searching("/usr/bin");

        await Assert.That(probe.Resolve(null)).IsNull();
        await Assert.That(probe.Resolve("")).IsNull();
        await Assert.That(probe.Resolve("   ")).IsNull();
        await Assert.That(probe.Finds(null)).IsFalse();
    }

    [Test]
    public async Task Finds_is_false_on_an_empty_search_path() {
        await Assert.That(BinaryProbe.Searching(null).Finds("claude")).IsFalse();
        await Assert.That(BinaryProbe.Searching("").Finds("claude")).IsFalse();
    }

    [Test]
    public async Task Resolve_is_null_when_a_bare_command_is_not_on_the_search_path() {
        using var tmp = new TempDir();

        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve($"kcap-absent-{Guid.NewGuid():N}")).IsNull();
    }

    [Test]
    public async Task Resolve_finds_a_bare_command_on_the_search_path() {
        using var tmp = new TempDir();
        var staged = await Stage(tmp.PathTo(Launchable("probe")));

        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve("probe")).IsEqualTo(staged, PathCasing);
        await Assert.That(BinaryProbe.Searching(tmp.Path).Finds("probe")).IsTrue();
    }

    [Test]
    public async Task Resolve_returns_a_fully_qualified_path_for_relative_input() {
        using var tmp = new TempDir();
        var staged = await Stage(tmp.PathTo(Launchable("probe")));

        // A path with a directory component but no root resolves against the cwd, not the search path.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), staged);
        var resolved = BinaryProbe.Searching(null).Resolve(relative);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(Path.IsPathFullyQualified(resolved!)).IsTrue();
        await Assert.That(resolved).IsEqualTo(staged);
    }

    /// <summary>An unusable entry must not abort the walk before later ones are tried — a missing
    /// directory and a stray-quote entry are both skipped, never fatal.</summary>
    [Test]
    public async Task Resolve_continues_past_unusable_search_path_entries() {
        using var tmp = new TempDir();
        var staged = await Stage(tmp.PathTo(Launchable("lateprobe")));
        var sep    = Path.PathSeparator;

        // Junk entries FIRST, so the good directory is only reached if the walk continued.
        var searchPath = $"{tmp.PathTo("nope")}{sep}\"quoted{sep}{tmp.Path}";

        await Assert.That(BinaryProbe.Searching(searchPath).Resolve("lateprobe")).IsEqualTo(staged, PathCasing);
    }

    [Test]
    public async Task Repeated_and_empty_entries_do_not_change_the_outcome() {
        using var tmp = new TempDir();
        var staged = await Stage(tmp.PathTo(Launchable("probe")));
        var sep    = Path.PathSeparator;

        await Assert.That(BinaryProbe.Searching($"{tmp.Path}{sep}{tmp.Path}{sep}{tmp.Path}").Resolve("probe"))
            .IsEqualTo(staged, PathCasing);
        await Assert.That(BinaryProbe.Searching($"{sep}{tmp.Path}").Resolve("probe")).IsEqualTo(staged, PathCasing);
    }

    /// <summary>Directories are searched in order, so the earlier copy of a command wins — which is
    /// what makes a search path an authority rather than a set.</summary>
    [Test]
    public async Task The_first_directory_holding_the_command_wins() {
        using var tmp = new TempDir();
        var first     = tmp.CreateDir("first");
        var second    = tmp.CreateDir("second");
        var winner    = await Stage(first.PathTo(Launchable("twinprobe")));
        await Stage(second.PathTo(Launchable("twinprobe")));

        await Assert.That(BinaryProbe.Searching($"{first}{Path.PathSeparator}{second}").Resolve("twinprobe"))
            .IsEqualTo(winner, PathCasing);
    }

    /// <summary>Bare, because it repoints the process's own PATH — which any concurrently spawned
    /// child would inherit.</summary>
    [Test, NotInParallel]
    public async Task FromEnvironment_searches_the_process_path() {
        using var tmp = new TempDir();
        var staged = await Stage(tmp.PathTo(Launchable("envprobe")));

        using var path = EnvScope.Exclusive("PATH", tmp.Path);

        await Assert.That(BinaryProbe.FromEnvironment().Resolve("envprobe")).IsEqualTo(staged, PathCasing);
        await Assert.That(BinaryProbe.OnPath("envprobe")).IsTrue();
        await Assert.That(BinaryProbe.OnPath($"kcap-absent-{Guid.NewGuid():N}")).IsFalse();
    }

    // ── Unix launch rules ──

    [Test, ExcludeOn(OS.Windows)] // no exec bit to withhold
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_requires_any_execute_bit() {
        using var tmp = new TempDir();
        var nonExec = tmp.PathTo("probe-nonexec");
        await File.WriteAllTextAsync(nonExec, "not executable");
        File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.That(BinaryProbe.Searching(tmp.Path).Finds("probe-nonexec")).IsFalse();
        await Assert.That(BinaryProbe.Searching(null).Resolve(nonExec)).IsNull();
    }

    // ── Windows launch rules ──

    /// <summary>A bare command backed only by a <c>.cmd</c> shim must resolve to that shim's full
    /// path, because <c>CreateProcess</c> will not find it otherwise.</summary>
    [Test, RunOn(OS.Windows)] // .cmd shims are an npm-on-Windows artifact
    public async Task Windows_resolves_a_bare_command_to_its_cmd_shim() {
        using var tmp = new TempDir();
        var shim = tmp.PathTo("shimprobe.cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\n");

        // PATHEXT is conventionally uppercase, so the resolved path won't match the on-disk
        // casing — harmless, since Windows paths are case-insensitive and CreateProcess takes either.
        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve("shimprobe"))
            .IsEqualTo(shim, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>npm drops an extensionless Git-Bash shim right beside <c>codex.cmd</c>, and
    /// <c>CreateProcess</c> cannot run it, so with both present the <c>.cmd</c> wins — whether the
    /// command arrives bare or as a rooted extensionless path.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Windows_prefers_the_cmd_over_its_extensionless_twin() {
        using var tmp = new TempDir();
        var shim = tmp.PathTo("codex");
        var cmd  = tmp.PathTo("codex.cmd");
        await File.WriteAllTextAsync(shim, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(cmd, "@echo off\r\n");

        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve("codex"))
            .IsEqualTo(cmd, StringComparison.OrdinalIgnoreCase);
        await Assert.That(BinaryProbe.Searching(null).Resolve(shim))
            .IsEqualTo(cmd, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The extensionless <c>#!/bin/sh</c> twin npm leaves beside the shim is not a hit on
    /// its own: <c>CreateProcess</c> launches through an extension, so a file PATHEXT does not name
    /// is unspawnable however present it looks — reporting it would advertise a vendor whose every
    /// launch then fails with error 193.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Windows_ignores_a_file_with_no_launchable_extension() {
        using var tmp = new TempDir();
        var shim      = tmp.PathTo("shprobe");
        // An extension PATHEXT cannot plausibly carry: a real one (.py) is added by its own
        // installer, so the assertion would turn on what else the host has installed.
        var unlisted  = tmp.PathTo("shprobe.kcapnope");
        await File.WriteAllTextAsync(shim, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(unlisted, "#!/bin/sh\nexit 0\n");

        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve("shprobe")).IsNull();
        await Assert.That(BinaryProbe.Searching(null).Resolve(shim)).IsNull();
        await Assert.That(BinaryProbe.Searching(tmp.Path).Resolve("shprobe.kcapnope")).IsNull();
        await Assert.That(BinaryProbe.Searching(tmp.Path).Finds("shprobe")).IsFalse();
    }

    /// <summary>A configured <c>daemon.codex_path</c> may omit the extension; that must still land
    /// on the <c>.cmd</c> beside it rather than reporting "not installed".</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Windows_appends_an_extension_to_a_rooted_path() {
        using var tmp = new TempDir();
        var stem = tmp.PathTo("codex");
        await File.WriteAllTextAsync(stem + ".cmd", "@echo off\r\n");

        await Assert.That(BinaryProbe.Searching(null).Resolve(stem))
            .IsEqualTo(stem + ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name this host will launch a bare <paramref name="stem"/> through.</summary>
    static string Launchable(string stem) => OperatingSystem.IsWindows() ? stem + ".cmd" : stem;

    /// <summary>How to compare a resolved path with a staged one. A bare name resolves through
    /// PATHEXT, which is conventionally uppercase, so on Windows the extension's casing is the
    /// variable's rather than the disk's — and paths there are case-insensitive.</summary>
    static StringComparison PathCasing =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Writes a launchable file at <paramref name="path"/> and returns it.</summary>
    static async Task<string> Stage(string path) {
        await File.WriteAllTextAsync(path, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\nexit 0\n");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
              | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );

        return path;
    }
}
