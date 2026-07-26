using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests <see cref="FailedLaunchLog"/> — the post-mortem retained tail of a failed hosted-agent
/// launch. A failed launch tears down the worktree and drops the in-memory PTY buffer, so the
/// terminal output (e.g. the consent dialog the reviewer wedged on) was invisible afterwards. This
/// persists the last N KB under <c>{state}/agents/failed/</c> so the next incident is a 2-minute
/// <c>cat</c> instead of an hours-long blind debug.
/// </summary>
public class FailedLaunchLogTests {
    static string TempDir() {
        var d = Path.Combine(Path.GetTempPath(), "kcap-faillog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Test]
    public async Task Persist_writes_a_retained_file_containing_the_output_and_reason() {
        var dir = TempDir();
        try {
            var log = new FailedLaunchLog(dir);

            var path = log.Persist("agent-1", Encoding.UTF8.GetBytes("WARNING: Bypass Permissions mode\n2. Yes, I accept\n"), "wedged on the consent dialog");

            await Assert.That(path).IsNotNull();
            await Assert.That(File.Exists(path!)).IsTrue();

            // Lives under the retained failed/ directory, NOT the worktree that gets deleted.
            await Assert.That(path!.Replace('\\', '/')).Contains("/agents/failed/");

            var content = await File.ReadAllTextAsync(path!);
            await Assert.That(content).Contains("2. Yes, I accept");           // the PTY tail
            await Assert.That(content).Contains("wedged on the consent dialog"); // the reason header
            await Assert.That(content).Contains("agent-1");                     // the agent id header
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Persist_keeps_only_the_last_N_bytes_of_a_large_stream() {
        var dir = TempDir();
        try {
            // 8 KB cap; feed 20 KB where only the tail carries the smoking gun.
            var log = new FailedLaunchLog(dir, maxBytes: 8 * 1024);

            var head = new string('A', 20 * 1024);
            var payload = head + "TAIL-MARKER-2. Yes, I accept";
            var path = log.Persist("big", Encoding.UTF8.GetBytes(payload), "startup failure");

            var content = await File.ReadAllTextAsync(path!);
            await Assert.That(content).Contains("TAIL-MARKER-2. Yes, I accept"); // tail retained
            // The body was truncated: nowhere near the full 20 KB of 'A's survives.
            await Assert.That(content.Length < 12 * 1024).IsTrue();
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Persist_is_resilient_to_empty_output() {
        var dir = TempDir();
        try {
            var log = new FailedLaunchLog(dir);

            var path = log.Persist("empty", [], "process exited before any output");

            await Assert.That(path).IsNotNull();
            var content = await File.ReadAllTextAsync(path!);
            await Assert.That(content).Contains("process exited before any output");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Persist_creates_an_owner_only_file_in_an_owner_only_directory() {
        // The retained tail can hold the reviewer's prompt, tool output, and any secrets the PTY
        // echoed, so it must not be world/group readable. Unix-only: file modes are a no-op on Windows.
        if (OperatingSystem.IsWindows()) return;

        var dir = TempDir();
        try {
            var log = new FailedLaunchLog(dir);

            var path = log.Persist("agent-perms", Encoding.UTF8.GetBytes("secret PTY tail"), "wedged");

            await Assert.That(path).IsNotNull();

            const UnixFileMode ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            const UnixFileMode ownerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            await Assert.That(File.GetUnixFileMode(path!)).IsEqualTo(ownerOnlyFile);
            await Assert.That(File.GetUnixFileMode(log.Dir)).IsEqualTo(ownerOnlyDir);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Persist_uses_a_path_safe_filename_for_a_hostile_agent_id() {
        var dir = TempDir();
        try {
            var log = new FailedLaunchLog(dir);

            // An agent id that crosses the SignalR wire unconstrained must never escape the dir.
            var path = log.Persist("../../etc/passwd", Encoding.UTF8.GetBytes("x"), "reason");

            await Assert.That(path).IsNotNull();
            var full = Path.GetFullPath(path!);
            var failedRoot = Path.GetFullPath(Path.Combine(dir, "agents", "failed"));
            await Assert.That(full.StartsWith(failedRoot, StringComparison.Ordinal)).IsTrue();
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
