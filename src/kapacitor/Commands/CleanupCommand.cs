namespace kapacitor.Commands;

static class CleanupCommand {
    public static async Task<int> HandleCleanup() {
        var watcherDir = PathHelpers.ConfigPath("watchers");

        if (!Directory.Exists(watcherDir)) {
            await Console.Out.WriteLineAsync("No watchers directory found.");

            return 0;
        }

        var pidFiles = Directory.GetFiles(watcherDir, "*.pid");

        if (pidFiles.Length == 0) {
            await Console.Out.WriteLineAsync("No watcher PID files found.");

            return 0;
        }

        var killed  = 0;
        var cleaned = 0;

        foreach (var pidFile in pidFiles) {
            var key        = Path.GetFileNameWithoutExtension(pidFile);
            var wasRunning = await WatcherManager.KillWatcher(key);

            if (wasRunning) {
                await Console.Out.WriteLineAsync($"Killed watcher {key}");
                killed++;
            } else {
                await Console.Out.WriteLineAsync($"Cleaned up stale PID file for {key}");
                cleaned++;
            }
        }

        await Console.Out.WriteLineAsync($"Done. Killed {killed} watcher(s), cleaned {cleaned} stale PID file(s).");

        return 0;
    }
}
