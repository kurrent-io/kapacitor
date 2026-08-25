using System.Runtime.InteropServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.Config;

/// <summary>The persisted list of repo paths (<c>repos.json</c>) under the <see cref="ConfigRoot"/>
/// it is handed. Writes are atomic (temp + rename) so a reader never observes a partial file.</summary>
public sealed class RepoPathStore(ConfigRoot config) {
    string StorePath { get; } = config.Path("repos.json");

    // Static: serialises the read-modify-write for the whole process however many instances exist.
    static readonly SemaphoreSlim Lock = new(1, 1);

    public static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task<RepoEntry[]> LoadAsync() {
        if (!File.Exists(StorePath))
            return [];

        try {
            var json = await File.ReadAllTextAsync(StorePath);
            return Collapse(JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.RepoEntryArray) ?? []);
        } catch {
            return [];
        }
    }

    /// Worktree entries written before AddAsync resolved them (GH #655) collapse on read into
    /// their main repository, newest last_used winning — so historical pollution disappears from
    /// every consumer without a migration, and the next write persists the cleaned list.
    static RepoEntry[] Collapse(RepoEntry[] entries) {
        if (entries.Length == 0) return entries;

        var comparer = PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var byRepo = new Dictionary<string, RepoEntry>(comparer);
        foreach (var entry in entries) {
            var resolved = NormalizePath(GitRepository.ResolveMainRepoRoot(entry.Path));
            if (!byRepo.TryGetValue(resolved, out var existing) || entry.LastUsed > existing.LastUsed)
                byRepo[resolved] = entry with { Path = resolved };
        }

        return [..byRepo.Values];
    }

    public async Task AddAsync(string path) {
        // A linked worktree registers as its main repository: user-facing repo lists show actual
        // repositories, and review flows launching into a requester's worktree must not mint a
        // "known repo" out of it (GH #655).
        var normalized = NormalizePath(GitRepository.ResolveMainRepoRoot(path));

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

    public async Task<bool> RemoveAsync(string path) {
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

    async Task SaveAsync(List<RepoEntry> entries) {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $"repos.{Environment.ProcessId}.tmp");
        var sorted   = entries.OrderByDescending(e => e.LastUsed).ToArray();
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(sorted, CapacitorJsonContext.Default.RepoEntryArray));
        File.Move(tempPath, StorePath, overwrite: true);
    }

    /// <summary>
    /// Returns all persisted repo paths sorted by last_used descending.
    /// </summary>
    public async Task<string[]> GetSortedPathsAsync() {
        var entries = await LoadAsync();
        return entries.OrderByDescending(e => e.LastUsed).Select(e => e.Path).ToArray();
    }
}
