using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

/// <summary>
/// <paramref name="FetchedRef"/> is the per-worktree ref the daemon fetched
/// the requested <c>baseRef</c> into (e.g. <c>refs/kapacitor/review/{name}</c>).
/// Tracked so <see cref="WorktreeManager.RemoveAsync"/> can delete it on
/// cleanup. Null for non-review launches.
/// </summary>
public record WorktreeInfo(string Path, string Branch, string SourceRepo, bool IsStandalone = false, string? FetchedRef = null);

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null, string? baseRef = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        // Place worktrees under the repo's own .capacitor/ directory so they inherit
        // the repo's workspace trust in Claude Code (trust cascades up parent dirs).
        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(worktreeRoot);

        if (await IsGitRepoWithCommits(repoPath)) {
            if (!string.IsNullOrEmpty(baseRef)) {
                // Fetch into a per-worktree ref instead of the shared FETCH_HEAD
                // so concurrent review launches in the same source repo can't
                // race on each other's fetches. The unique ref carries the
                // worktree name so it's traceable and easy to clean up.
                var fetchedRef = $"refs/kapacitor/review/{name}";
                await RunGit(repoPath, FetchTimeout, "fetch", "origin", $"{baseRef}:{fetchedRef}");
                await RunGit(repoPath, GitTimeout, "worktree", "add", "-B", branch, worktreePath, fetchedRef);

                return new WorktreeInfo(worktreePath, branch, repoPath, FetchedRef: fetchedRef);
            }

            await RunGit(repoPath, GitTimeout, "worktree", "add", worktreePath, "-b", branch);

            return new WorktreeInfo(worktreePath, branch, repoPath);
        }

        // Standalone: copy files + git init
        Directory.CreateDirectory(worktreePath);
        CopyDirectory(repoPath, worktreePath);
        await RunGit(worktreePath, GitTimeout, "init");
        await RunGit(worktreePath, GitTimeout, "add", "-A");
        await RunGit(worktreePath, GitTimeout, "commit", "-m", "Initial snapshot");

        return new WorktreeInfo(worktreePath, "", repoPath, IsStandalone: true);
    }

    public static async Task RemoveAsync(WorktreeInfo worktree, bool deleteBranch = true) {
        if (worktree.IsStandalone) {
            if (Directory.Exists(worktree.Path)) {
                Directory.Delete(worktree.Path, true);
            }

            return;
        }

        await RunGit(worktree.SourceRepo, GitTimeout, "worktree", "remove", worktree.Path, "--force");

        if (deleteBranch && !string.IsNullOrEmpty(worktree.Branch)) {
            await RunGitBestEffort(worktree.SourceRepo, "branch", "-D", worktree.Branch);
        }

        if (!string.IsNullOrEmpty(worktree.FetchedRef)) {
            await RunGitBestEffort(worktree.SourceRepo, "update-ref", "-d", worktree.FetchedRef);
        }
    }

    public Task CleanupOrphanedAsync(IEnumerable<string>? activeWorktreePaths = null) {
        // Legacy global root — clean up any leftover worktrees from before the per-repo change
        var worktreePaths = activeWorktreePaths as string[] ?? [..activeWorktreePaths ?? []];
        CleanupDirectory(config.WorktreeRoot, worktreePaths);

        // Per-repo roots — scan each allowed repo for .capacitor/worktrees/
        foreach (var repoPath in config.AllowedRepoPaths) {
            var cleanPath = repoPath.TrimEnd('/', '*');
            var perRepoRoot = Path.Combine(cleanPath, ".capacitor", "worktrees");
            CleanupDirectory(perRepoRoot, worktreePaths);
        }

        return Task.CompletedTask;
    }

    void CleanupDirectory(string root, IEnumerable<string>? activeWorktreePaths) {
        if (!Directory.Exists(root)) return;

        var activePaths = activeWorktreePaths?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var dir in Directory.GetDirectories(root)) {
            if (activePaths.Contains(dir)) continue;

            LogCleaningUp(dir);
            try { Directory.Delete(dir, true); } catch (Exception ex) { LogCleanupFailed(ex, dir); }
        }
    }

    /// <summary>Default timeout for local git operations (worktree add, init, commit, …).</summary>
    static readonly TimeSpan GitTimeout   = TimeSpan.FromSeconds(60);

    /// <summary>Longer timeout for network git operations (fetch).</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    static async Task<bool> IsGitRepoWithCommits(string path) {
        try {
            var psi = NewGitPsi(path, ["rev-parse", "HEAD"]);
            using var proc = Process.Start(psi)!;
            using var cts  = new CancellationTokenSource(GitTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch { /* best-effort */ }

                return false;
            }

            return proc.ExitCode == 0;
        } catch { return false; }
    }

    static async Task RunGit(string cwd, TimeSpan timeout, params string[] args) {
        var psi = NewGitPsi(cwd, args);
        using var proc = Process.Start(psi)!;
        using var cts  = new CancellationTokenSource(timeout);

        try {
            await proc.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { proc.Kill(true); } catch { /* best-effort */ }

            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s"
            );
        }

        if (proc.ExitCode != 0) {
            var stderr = await proc.StandardError.ReadToEndAsync();

            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }

    static async Task RunGitBestEffort(string cwd, params string[] args) {
        try { await RunGit(cwd, GitTimeout, args); } catch {
            /* best-effort */
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for git with prompts disabled
    /// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=Never</c>) so an
    /// unattended daemon can never block on a credential prompt.
    /// </summary>
    static ProcessStartInfo NewGitPsi(string cwd, string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"]     = "Never";
        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaning up orphaned worktree: {Path}")]
    partial void LogCleaningUp(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clean up {Path}")]
    partial void LogCleanupFailed(Exception ex, string path);

    static void CopyDirectory(string source, string dest) {
        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            if (Path.GetFileName(dir) == ".git") {
                continue;
            }

            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }
    }
}
