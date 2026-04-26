using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

public record WorktreeInfo(string Path, string Branch, string SourceRepo, bool IsStandalone = false);

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];

        // Place worktrees under the repo's own .capacitor/ directory so they inherit
        // the repo's workspace trust in Claude Code (trust cascades up parent dirs).
        var worktreeRoot = Path.Combine(repoPath, ".capacitor", "worktrees");
        var worktreePath = Path.Combine(worktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(worktreeRoot);

        if (await IsGitRepoWithCommits(repoPath)) {
            await RunGit(repoPath, "worktree", "add", worktreePath, "-b", branch);

            return new WorktreeInfo(worktreePath, branch, repoPath);
        }

        // Standalone: copy files + git init
        Directory.CreateDirectory(worktreePath);
        CopyDirectory(repoPath, worktreePath);
        await RunGit(worktreePath, "init");
        await RunGit(worktreePath, "add", "-A");
        await RunGit(worktreePath, "commit", "-m", "Initial snapshot");

        return new WorktreeInfo(worktreePath, "", repoPath, IsStandalone: true);
    }

    public static async Task RemoveAsync(WorktreeInfo worktree, bool deleteBranch = true) {
        if (worktree.IsStandalone) {
            if (Directory.Exists(worktree.Path)) {
                Directory.Delete(worktree.Path, true);
            }

            return;
        }

        await RunGit(worktree.SourceRepo, "worktree", "remove", worktree.Path, "--force");

        if (deleteBranch && !string.IsNullOrEmpty(worktree.Branch)) {
            await RunGitBestEffort(worktree.SourceRepo, "branch", "-D", worktree.Branch);
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

    static async Task<bool> IsGitRepoWithCommits(string path) {
        try {
            var psi = new ProcessStartInfo("git", ["rev-parse", "HEAD"]) {
                WorkingDirectory       = path,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0;
        } catch { return false; }
    }

    static async Task RunGit(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0) {
            var stderr = await proc.StandardError.ReadToEndAsync();

            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }

    static async Task RunGitBestEffort(string cwd, params string[] args) {
        try { await RunGit(cwd, args); } catch {
            /* best-effort */
        }
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
