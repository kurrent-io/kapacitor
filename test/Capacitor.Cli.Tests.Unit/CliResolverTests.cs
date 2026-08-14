using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

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
        var tempFile = Path.Combine(Path.GetTempPath(), $"cli-resolver-test-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(tempFile, "#!/bin/sh\necho hi\n");
        MakeExecutable(tempFile);

        try {
            await Assert.That(CliResolver.Exists(tempFile)).IsTrue();
        } finally {
            File.Delete(tempFile);
        }
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

        var tempFile = Path.Combine(Path.GetTempPath(), $"cli-resolver-noexec-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(tempFile, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try {
            await Assert.That(CliResolver.Exists(tempFile)).IsFalse();
        } finally {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ReturnsFalse_WhenAbsolutePathMissing() {
        var missing = Path.Combine(Path.GetTempPath(), $"cli-resolver-missing-{Guid.NewGuid():N}");

        await Assert.That(CliResolver.Exists(missing)).IsFalse();
    }

    // NotInParallel: this test mutates the process-global PATH env var,
    // which races with any other test reading PATH (e.g. plugin installers,
    // resolver tests below). A unique group key isn't enough because
    // cross-group tests can still race; running fully alone is safe.
    [Test, NotInParallel]
    public async Task ReturnsTrue_WhenBareCommandResolvesOnPath() {
        // Drop a fake "kcap-pathprobe-{guid}" binary into a temp dir,
        // mark it executable on POSIX, and prepend that dir to PATH.
        var dir        = Directory.CreateTempSubdirectory("cli-resolver-path-").FullName;
        var name       = $"kcap-pathprobe-{Guid.NewGuid():N}";
        var binaryName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var binaryPath = Path.Combine(dir, binaryName);
        await File.WriteAllTextAsync(binaryPath, "");
        MakeExecutable(binaryPath);

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{dir}{Path.PathSeparator}{savedPath}");

        try {
            await Assert.That(CliResolver.Exists(name)).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The Unix exec-bit check (mirrors <c>AgentDetection.IsExecutable</c>)
    /// applies to PATH-resolved candidates too, not just absolute paths.
    /// </summary>
    [Test, NotInParallel]
    public async Task ReturnsFalse_WhenBareCommandOnPathIsNotExecutable() {
        if (OperatingSystem.IsWindows()) return;

        var dir        = Directory.CreateTempSubdirectory("cli-resolver-noexec-path-").FullName;
        var name       = $"kcap-pathprobe-noexec-{Guid.NewGuid():N}";
        var binaryPath = Path.Combine(dir, name);
        await File.WriteAllTextAsync(binaryPath, "");
        File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", $"{dir}{Path.PathSeparator}{savedPath}");

        try {
            await Assert.That(CliResolver.Exists(name)).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ReturnsFalse_WhenBareCommandNotOnPath() {
        var unlikely = $"kcap-not-installed-{Guid.NewGuid():N}";

        await Assert.That(CliResolver.Exists(unlikely)).IsFalse();
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
