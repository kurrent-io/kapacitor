using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Verifies the permission-request hook self-heals the transcript watcher
/// (<see cref="PermissionRequestCommand.TryEnsureWatcher"/>): a frequently-firing
/// mid-session recovery point that re-spawns a dead/never-started watcher so the
/// session does not stay stuck "active". Mirrors <see cref="WatcherLifecycleTests"/> —
/// uses <c>KCAP_WATCHER_DIR</c> and deliberately does NOT capture Console, since
/// spawning the watcher's child process corrupts TUnit's Console capture.
/// </summary>
[NotInParallel]
public class PermissionRequestWatcherSelfHealTests {
    static readonly TempDir Tmp = new();
    static readonly TempDir Transcripts = new();
    static string TempDir => Tmp.Path;

    static string? _previousWatcherDir;

    [Before(Class)]
    public static void SetUp() {
        _previousWatcherDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        // Restore any preexisting value rather than clobbering to null, so a test process
        // started with KCAP_WATCHER_DIR set isn't left altered for later test classes.
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", _previousWatcherDir);
        Tmp.Dispose();
        Transcripts.Dispose();
    }

    static (string sessionId, string transcriptPath, string pidFile) NewSession() {
        var sessionId      = $"permreq{Guid.NewGuid():N}";
        var transcriptPath = Transcripts.CreateFile($"{sessionId}.jsonl");

        return (sessionId, transcriptPath, Path.Combine(TempDir, $"{sessionId}.pid"));
    }

    [Test]
    public async Task SpawnsWatcher_WhenMainSessionTranscriptPresent() {
        var (sessionId, transcriptPath, pidFile) = NewSession();

        var node = new JsonObject {
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = "/tmp/test"
        };

        await PermissionRequestCommand.TryEnsureWatcher("http://localhost:0", sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsTrue();
        var lines = await File.ReadAllLinesAsync(pidFile);
        await Assert.That(int.TryParse(lines[0].Trim(), out _)).IsTrue();

        await Cli.WatcherManager.KillWatcher(sessionId);
    }

    [Test]
    public async Task SkipsWatcher_WhenAgentIdPresent() {
        // A present agent_id means a subagent tool call; its watcher uses a distinct
        // key + transcript and is ensured at subagent-start, so self-heal must not spawn here.
        var (sessionId, transcriptPath, pidFile) = NewSession();

        var node = new JsonObject {
            ["transcript_path"] = transcriptPath,
            ["agent_id"]        = "agent-123"
        };

        await PermissionRequestCommand.TryEnsureWatcher("http://localhost:0", sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsFalse();

        await Cli.WatcherManager.KillWatcher(sessionId);
    }

    [Test]
    public async Task SkipsWatcher_WhenTranscriptPathMissing() {
        var (sessionId, _, pidFile) = NewSession();

        var node = new JsonObject {
            ["cwd"] = "/tmp/test"
        };

        await PermissionRequestCommand.TryEnsureWatcher("http://localhost:0", sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsFalse();

        await Cli.WatcherManager.KillWatcher(sessionId);
    }
}
