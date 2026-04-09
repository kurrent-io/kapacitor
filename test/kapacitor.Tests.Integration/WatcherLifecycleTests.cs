namespace kapacitor.Tests.Integration;

[NotInParallel]
public class WatcherLifecycleTests {
    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "kapacitor-watcher-tests");

    [Before(Class)]
    public static void SetUp() {
        Directory.CreateDirectory(TempDir);
        Environment.SetEnvironmentVariable("KAPACITOR_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KAPACITOR_WATCHER_DIR", null);

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
            await WatcherManager.SpawnWatcher("http://localhost:0", key, transcriptPath, agentId: null);
            await AssertPidFileValid(pidFile);

            await WatcherManager.KillWatcher(key);
            await Assert.That(File.Exists(pidFile)).IsFalse();
        } finally {
            File.Delete(transcriptPath);
        }
    }

    [Test]
    public async Task EnsureWatcherRunning_SpawnsIfDead() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        try {
            await WatcherManager.EnsureWatcherRunning("http://localhost:0", key, transcriptPath, agentId: null);
            await AssertPidFileValid(pidFile);

            await WatcherManager.KillWatcher(key);
        } finally {
            File.Delete(transcriptPath);
        }
    }
}
