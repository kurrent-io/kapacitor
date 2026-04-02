namespace kapacitor.Commands;

static class CleanupCommand {
    public static async Task<int> HandleCleanup() {
        var watcherDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "kapacitor",
            "watchers"
        );

        if (!Directory.Exists(watcherDir)) {
            Console.WriteLine("No watchers directory found.");

            return 0;
        }

        var pidFiles = Directory.GetFiles(watcherDir, "*.pid");

        if (pidFiles.Length == 0) {
            Console.WriteLine("No watcher PID files found.");

            return 0;
        }

        var killed  = 0;
        var cleaned = 0;

        foreach (var pidFile in pidFiles) {
            var key        = Path.GetFileNameWithoutExtension(pidFile);
            var wasRunning = await WatcherManager.KillWatcher(key);

            if (wasRunning) {
                Console.WriteLine($"Killed watcher {key}");
                killed++;
            } else {
                Console.WriteLine($"Cleaned up stale PID file for {key}");
                cleaned++;
            }
        }

        Console.WriteLine($"Done. Killed {killed} watcher(s), cleaned {cleaned} stale PID file(s).");

        return 0;
    }
}
