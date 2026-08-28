using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Covers the daemon-startup vendor probe. The resolver is the
/// only place the daemon decides whether to advertise <c>claude</c> /
/// <c>codex</c> over <c>DaemonConnect</c>, so the launch dialog's vendor
/// filter is only ever as accurate as this lookup.
/// </summary>
public class CliResolverTests {
    [Test]
    public async Task ReturnsFalse_ForEmptyInput() {
        await Assert.That(CliResolver.Exists("")).IsFalse();
        await Assert.That(CliResolver.Exists("   ")).IsFalse();
    }

    [Test]
    public async Task ReturnsTrue_WhenAbsolutePathIsExecutable() {
        using var tmp = new TempDir();
        // Launchable means "carries an extension PATHEXT names" on Windows and "carries the execute
        // bit" on Unix; a configured path the OS cannot spawn must not be advertised as a CLI.
        var tempFile = tmp.CreateFile(Launchable("cli-resolver-test"), "#!/bin/sh\necho hi\n");
        MakeExecutable(tempFile);

        await Assert.That(CliResolver.Exists(tempFile)).IsTrue();
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

        await Assert.That(CliResolver.Exists(tempFile)).IsFalse();
    }

    [Test]
    public async Task ReturnsFalse_WhenAbsolutePathMissing() {
        var missing = Path.Combine(Path.GetTempPath(), $"cli-resolver-missing-{Guid.NewGuid():N}");

        await Assert.That(CliResolver.Exists(missing)).IsFalse();
    }

    [Test, NotInParallel]
    public async Task ReturnsTrue_WhenBareCommandResolvesOnPath() {
        // Drop a fake "kcap-pathprobe-{guid}" binary into a temp dir,
        // mark it executable on POSIX, and prepend that dir to PATH.
        using var tmp = new TempDir();
        var name       = $"kcap-pathprobe-{Guid.NewGuid():N}";
        var binaryPath = tmp.CreateFile(Launchable(name), "");
        MakeExecutable(binaryPath);

        var       savedPath = Environment.GetEnvironmentVariable("PATH");
        using var pathEnv   = EnvScope.Exclusive("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        await Assert.That(CliResolver.Exists(name)).IsTrue();
    }

    /// <summary>
    /// The Unix exec-bit check (mirrors <c>AgentDetection.IsExecutable</c>)
    /// applies to PATH-resolved candidates too, not just absolute paths.
    /// </summary>
    [Test, NotInParallel]
    public async Task ReturnsFalse_WhenBareCommandOnPathIsNotExecutable() {
        if (OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var name       = $"kcap-pathprobe-noexec-{Guid.NewGuid():N}";
        var binaryPath = tmp.CreateFile(name, "");
        File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var       savedPath = Environment.GetEnvironmentVariable("PATH");
        using var pathEnv   = EnvScope.Exclusive("PATH", $"{tmp.Path}{Path.PathSeparator}{savedPath}");

        await Assert.That(CliResolver.Exists(name)).IsFalse();
    }

    [Test]
    public async Task ReturnsFalse_WhenBareCommandNotOnPath() {
        var unlikely = $"kcap-not-installed-{Guid.NewGuid():N}";

        await Assert.That(CliResolver.Exists(unlikely)).IsFalse();
    }

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
