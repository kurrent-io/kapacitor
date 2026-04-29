using System.Collections.Concurrent;
using System.Diagnostics;
using kapacitor.Config;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

/// <summary>
/// Resolves which local checkout(s) on this daemon correspond to a given
/// <c>owner/repo</c>. Used by the server's "Review this PR" flow:
///   1. Server seeds candidate paths from session history (terminal sessions count).
///   2. Daemon merges those with its own knowledge — persisted <see cref="RepoPathStore"/>
///      plus the <see cref="DaemonConfig.AllowedRepoPaths"/> allowlist — and dedupes.
///   3. For each candidate it verifies the path exists on disk, walks up to the
///      nearest <c>.git</c> root, reads <c>origin</c>, and matches against
///      <c>owner/repo</c>.
///   4. All confirmed git roots are returned (the user picks which one to review on).
///
/// Normalized origin URLs are cached for <see cref="CacheTtl"/> per path to avoid
/// spawning <c>git</c> on rapid re-clicks.
/// </summary>
internal partial class RepoMatcher(DaemonConfig config, ILogger<RepoMatcher> logger) {
    static readonly TimeSpan CacheTtl    = TimeSpan.FromSeconds(60);
    const           int      MaxWalkUp   = 10;
    const           int      GitTimeoutMs = 5_000;

    readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<string[]> FindAsync(string owner, string repo, string[] serverCandidates, CancellationToken ct) {
        var target = $"github.com/{owner}/{repo}";

        var candidates = await MergeCandidatesAsync(serverCandidates);
        var matches    = new List<string>();
        var seenRoots  = new HashSet<string>(RepoPathStore.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates) {
            ct.ThrowIfCancellationRequested();

            try {
                var root = WalkUpToGitRoot(candidate);

                if (root is null || !seenRoots.Add(root)) {
                    continue;
                }

                var remote = await GetNormalizedOriginAsync(root, ct);

                if (remote is null) {
                    continue;
                }

                if (string.Equals(remote, target, StringComparison.OrdinalIgnoreCase)) {
                    matches.Add(root);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                LogCandidateFailed(ex, candidate);
            }
        }

        return [..matches];
    }

    /// <summary>
    /// Server-supplied candidates first (recency-sorted on the server side),
    /// followed by the daemon's persisted RepoPathStore and config-allowed paths.
    /// Dedupe is platform-aware via <see cref="RepoPathStore.PathComparison"/>.
    /// </summary>
    async Task<List<string>> MergeCandidatesAsync(string[] serverCandidates) {
        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var seen   = new HashSet<string>(comparer);
        var merged = new List<string>();

        void Add(string path) {
            if (string.IsNullOrWhiteSpace(path)) return;

            var normalized = TryNormalize(path);
            if (normalized is null) return;

            if (seen.Add(normalized)) merged.Add(normalized);
        }

        foreach (var p in serverCandidates) Add(p);

        // Persisted RepoPathStore failures are non-fatal — server candidates
        // and AllowedRepoPaths still flow through.
        try {
            var persisted = await RepoPathStore.GetSortedPathsAsync();
            foreach (var p in persisted) Add(p);
        } catch (Exception ex) {
            LogPersistedLoadFailed(ex);
        }

        foreach (var p in config.AllowedRepoPaths) {
            Add(p.TrimEnd('/', '*'));
        }

        return merged;
    }

    static string? TryNormalize(string path) {
        try {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Walks up at most <see cref="MaxWalkUp"/> levels looking for a <c>.git</c>
    /// directory or file (file = worktree-style gitdir). Returns the directory
    /// containing it, or null if no git root is found within the limit.
    /// </summary>
    static string? WalkUpToGitRoot(string startPath) {
        if (!Directory.Exists(startPath)) return null;

        var current = startPath;

        for (var i = 0; i < MaxWalkUp; i++) {
            var gitPath = Path.Combine(current, ".git");

            if (Directory.Exists(gitPath) || File.Exists(gitPath)) {
                return current;
            }

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) {
                return null;
            }

            current = parent;
        }

        return null;
    }

    async Task<string?> GetNormalizedOriginAsync(string repoRoot, CancellationToken ct) {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(repoRoot, out var cached) && cached.Expires > now) {
            return cached.NormalizedRemote;
        }

        var raw = await RunGitCaptureAsync(repoRoot, ["remote", "get-url", "origin"], ct);
        var normalized = raw is null ? null : RemoteMatcher.NormalizeRemoteUrl(raw.Trim());

        _cache[repoRoot] = new CacheEntry(now + CacheTtl, normalized);

        // Opportunistic sweep: a cache miss means we just went over the
        // network/disk anyway, so dropping any other expired keys is cheap.
        // Bounds the dictionary in long-running daemons that probe many
        // distinct paths.
        EvictExpired(now);

        return normalized;
    }

    void EvictExpired(DateTimeOffset now) {
        foreach (var kvp in _cache) {
            if (kvp.Value.Expires <= now) _cache.TryRemove(kvp.Key, out _);
        }
    }

    static async Task<string?> RunGitCaptureAsync(string cwd, string[] args, CancellationToken ct) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"]     = "Never";

        using var proc = Process.Start(psi);

        if (proc is null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GitTimeoutMs);

        try {
            await proc.WaitForExitAsync(timeoutCts.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch { /* best-effort */ }

            if (ct.IsCancellationRequested) throw;

            return null; // git timed out
        }

        if (proc.ExitCode != 0) return null;

        var output = await proc.StandardOutput.ReadToEndAsync(ct);

        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    record CacheEntry(DateTimeOffset Expires, string? NormalizedRemote);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to inspect candidate path {Path}")]
    partial void LogCandidateFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to load persisted RepoPathStore entries")]
    partial void LogPersistedLoadFailed(Exception ex);
}
