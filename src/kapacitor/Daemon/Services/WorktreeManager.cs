using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

public record WorktreeInfo(string Path, string Branch, string SourceRepo, bool IsStandalone = false);

public partial class WorktreeManager(DaemonConfig config, ILogger<WorktreeManager> logger) {
    public async Task<WorktreeInfo> CreateAsync(string repoPath, string? name = null) {
        name ??= $"agent-{Guid.NewGuid():N}"[..20];
        var worktreePath = Path.Combine(config.WorktreeRoot, name);
        var branch       = $"capacitor/{name}";

        Directory.CreateDirectory(config.WorktreeRoot);

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
        if (!Directory.Exists(config.WorktreeRoot)) {
            return Task.CompletedTask;
        }

        var activePaths = activeWorktreePaths?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var dir in Directory.GetDirectories(config.WorktreeRoot)) {
            if (activePaths.Contains(dir)) {
                continue;
            }

            LogCleaningUp(dir);
            try { Directory.Delete(dir, true); } catch (Exception ex) { LogCleanupFailed(ex, dir); }
        }

        return Task.CompletedTask;
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
