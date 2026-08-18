using System.Diagnostics;

namespace Capacitor.Cli.Core.Tests.Unit;

public class CliProcessTests {
    // Regression: a 1-in-111 what's-done generation crashed (SIGABRT) because
    // `Process.Start("claude")` threw Win32Exception ("No such file or
    // directory") when `claude` wasn't on the detached process's PATH, and the
    // throw escaped to Main and aborted the whole CLI. TryStart must convert a
    // failed launch into a logged null so callers fall through their existing
    // "process is null → return null" path instead of crashing the process.
    [Test]
    public async Task TryStart_MissingExecutable_ReturnsNullAndLogs() {
        // Unique absolute path that cannot exist — deterministic ENOENT on every
        // OS, independent of the test host's PATH and free of collisions with any
        // pre-existing temp file.
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-nonexistent-binary-{Guid.NewGuid():N}");
        await Assert.That(File.Exists(missing)).IsFalse();

        var psi = new ProcessStartInfo {
            FileName               = missing,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
        };
        var logs = new List<string>();

        var process = CliProcess.TryStart(psi, logs.Add);

        await Assert.That(process).IsNull();
        await Assert.That(logs).Contains(l => l.Contains("Failed to start", StringComparison.Ordinal));
    }
}
