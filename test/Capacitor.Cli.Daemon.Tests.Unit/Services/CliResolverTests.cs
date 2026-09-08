using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Covers the daemon-startup vendor probe. The resolver is the
/// only place the daemon decides whether to advertise <c>claude</c> /
/// <c>codex</c> over <c>DaemonConnect</c>, so the launch dialog's vendor
/// filter is only ever as accurate as this lookup — over the search path the
/// injected probe carries, never the process's own.
/// </summary>
public class CliResolverTests {
    [Test]
    public async Task ReturnsFalse_ForEmptyInput() {
        await Assert.That(Searching(null).Exists("")).IsFalse();
        await Assert.That(Searching(null).Exists("   ")).IsFalse();
    }

    [Test]
    public async Task ReturnsTrue_WhenAbsolutePathIsExecutable() {
        using var tmp = new TempDir();
        // Launchable means "carries an extension PATHEXT names" on Windows and "carries the execute
        // bit" on Unix; a configured path the OS cannot spawn must not be advertised as a CLI.
        var tempFile = tmp.CreateFile(Launchable("cli-resolver-test"), "#!/bin/sh\necho hi\n");
        MakeExecutable(tempFile);

        await Assert.That(Searching(null).Exists(tempFile)).IsTrue();
    }

    /// <summary>
    /// A non-executable file on disk must NOT be advertised as a spawnable
    /// CLI. The original resolver missed this and would have shipped
    /// "claude" as supported on hosts where the binary existed but couldn't
    /// be executed (e.g. wrong owner, stripped exec bit).
    /// </summary>
    [Test]
    public async Task ReturnsFalse_WhenAbsolutePathIsNotExecutable() {
        if (OperatingSystem.IsWindows()) return; // Windows has no exec bit

        using var tmp = new TempDir();
        var tempFile = tmp.CreateFile("cli-resolver-noexec", "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await Assert.That(Searching(null).Exists(tempFile)).IsFalse();
    }

    [Test]
    public async Task ReturnsFalse_WhenAbsolutePathMissing() {
        using var missingDir = TempDir.WithPathTo("cli-resolver-missing", out var missing);

        await Assert.That(Searching(null).Exists(missing)).IsFalse();
    }

    [Test]
    public async Task ReturnsTrue_WhenBareCommandResolvesOnTheSearchPath() {
        using var tmp = new TempDir();
        var name       = $"kcap-pathprobe-{Guid.NewGuid():N}";
        var binaryPath = tmp.CreateFile(Launchable(name), "");
        MakeExecutable(binaryPath);

        await Assert.That(Searching(tmp.Path).Exists(name)).IsTrue();
    }

    /// <summary>
    /// The Unix exec-bit check (mirrors <c>AgentDetection.IsExecutable</c>)
    /// applies to PATH-resolved candidates too, not just absolute paths.
    /// </summary>
    [Test]
    public async Task ReturnsFalse_WhenBareCommandOnPathIsNotExecutable() {
        if (OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var name       = $"kcap-pathprobe-noexec-{Guid.NewGuid():N}";
        var binaryPath = tmp.CreateFile(name, "");
        File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await Assert.That(Searching(tmp.Path).Exists(name)).IsFalse();
    }

    [Test]
    public async Task ReturnsFalse_WhenBareCommandNotOnPath() {
        var unlikely = $"kcap-not-installed-{Guid.NewGuid():N}";

        await Assert.That(Searching(null).Exists(unlikely)).IsFalse();
    }

    /// <summary>A resolver over exactly <paramref name="searchPath"/>.</summary>
    static CliResolver Searching(string? searchPath) => new(BinaryProbe.Searching(searchPath));

    /// <summary>The name this host will launch <paramref name="stem"/> through: Windows spawns
    /// through an extension PATHEXT names, Unix through the execute bit.</summary>
    static string Launchable(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

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
