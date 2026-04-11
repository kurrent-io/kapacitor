using System.Runtime.InteropServices;
using System.Text.Json;

namespace kapacitor.Config;

static class RepoPathStore {
    static readonly string StorePath = PathHelpers.ConfigPath("repos.json");

    static readonly SemaphoreSlim Lock = new(1, 1);

    static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static async Task<RepoEntry[]> LoadAsync() {
        if (!File.Exists(StorePath))
            return [];

        try {
            var json = await File.ReadAllTextAsync(StorePath);
            return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.RepoEntryArray) ?? [];
        } catch {
            return [];
        }
    }

    public static async Task AddAsync(string path) {
        var normalized = NormalizePath(path);

        await Lock.WaitAsync();

        try {
            var entries  = (await LoadAsync()).ToList();
            var existing = entries.FindIndex(e => string.Equals(e.Path, normalized, PathComparison));

            if (existing >= 0) {
                entries[existing] = entries[existing] with { LastUsed = DateTimeOffset.UtcNow };
            } else {
                entries.Add(new RepoEntry { Path = normalized, LastUsed = DateTimeOffset.UtcNow });
            }

            await SaveAsync(entries);
        } finally {
            Lock.Release();
        }
    }

    public static async Task<bool> RemoveAsync(string path) {
        var normalized = NormalizePath(path);

        await Lock.WaitAsync();

        try {
            var entries = (await LoadAsync()).ToList();
            var removed = entries.RemoveAll(e => string.Equals(e.Path, normalized, PathComparison));

            if (removed == 0) return false;

            await SaveAsync(entries);
            return true;
        } finally {
            Lock.Release();
        }
    }

    static async Task SaveAsync(List<RepoEntry> entries) {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $"repos.{Environment.ProcessId}.tmp");
        var sorted   = entries.OrderByDescending(e => e.LastUsed).ToArray();
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(sorted, KapacitorJsonContext.Default.RepoEntryArray));
        File.Move(tempPath, StorePath, overwrite: true);
    }

    /// <summary>
    /// Returns all persisted repo paths sorted by last_used descending.
    /// </summary>
    public static async Task<string[]> GetSortedPathsAsync() {
        var entries = await LoadAsync();
        return entries.OrderByDescending(e => e.LastUsed).Select(e => e.Path).ToArray();
    }
}
