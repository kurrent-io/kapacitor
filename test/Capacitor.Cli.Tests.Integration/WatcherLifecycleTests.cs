namespace Capacitor.Cli.Tests.Integration;

[NotInParallel]
public class WatcherLifecycleTests {
    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "kcap-watcher-tests");

    [Before(Class)]
    public static void SetUp() {
        Directory.CreateDirectory(TempDir);
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", null);

        try { Directory.Delete(TempDir, recursive: true); } catch {
            /* best effort */
        }
    }

    static (string key, string transcriptPath, string pidFile) SetUpWatcher() {
        var key            = $"test-watcher-{Guid.NewGuid():N}";
        var transcriptPath = Path.Combine(Path.GetTempPath(), $"{key}.jsonl");
        File.WriteAllText(transcriptPath, "");

        return (key, transcriptPath, Path.Combine(TempDir, $"{key}.pid"));
    }

    static async Task AssertPidFileValid(string pidFile) {
        await Assert.That(File.Exists(pidFile)).IsTrue();
        var pidText = await File.ReadAllTextAsync(pidFile);
        await Assert.That(int.TryParse(pidText.Trim(), out _)).IsTrue();
    }

    [Test]
    public async Task SpawnAndKill_ManagesPidFile() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        try {
            await Cli.WatcherManager.SpawnWatcher("http://localhost:0", key, transcriptPath, agentId: null);
            await AssertPidFileValid(pidFile);

            await Cli.WatcherManager.KillWatcher(key);
            await Assert.That(File.Exists(pidFile)).IsFalse();
        } finally {
            File.Delete(transcriptPath);
        }
    }

    [Test]
    public async Task EnsureWatcherRunning_SpawnsIfDead() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        try {
            await Cli.WatcherManager.EnsureWatcherRunning("http://localhost:0", key, transcriptPath, agentId: null);
            await AssertPidFileValid(pidFile);

            await Cli.WatcherManager.KillWatcher(key);
        } finally {
            File.Delete(transcriptPath);
        }
    }

    // #550: a session watcher must stop the child watchers it spawned on its own way out — they
    // have no parent-pid watchdog and the server's StopWatcher only reaches the session watcher's
    // connection, so the parent's teardown is the only thing that knows they exist. One live and
    // one already-dead child in the same batch: both pid files must be gone afterwards (the dead
    // entry is swept, never an error that aborts the batch).
    [Test]
    public async Task KillWatchers_stops_every_tracked_child_and_clears_their_pid_files() {
        var (liveKey, liveTranscript, livePidFile) = SetUpWatcher();
        var (deadKey, deadTranscript, deadPidFile) = SetUpWatcher();

        try {
            await Cli.WatcherManager.SpawnWatcher("http://localhost:0", liveKey, liveTranscript, agentId: null);
            await AssertPidFileValid(livePidFile);

            // A pid that cannot belong to a live process — exercises the already-exited sweep arm.
            await File.WriteAllTextAsync(deadPidFile, "99999999");

            await Cli.WatcherManager.KillWatchers([liveKey, deadKey]);

            await Assert.That(File.Exists(livePidFile)).IsFalse();
            await Assert.That(File.Exists(deadPidFile)).IsFalse();
        } finally {
            File.Delete(liveTranscript);
            File.Delete(deadTranscript);
        }
    }
}
