using System.Diagnostics;
using kapacitor.Daemon;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Validates <see cref="WorktreeManager.CreateAsync"/> with a baseRef param.
/// We build a real local git repo with two commits on a side ref so we can
/// fetch it back as if it were a PR head and assert the worktree HEAD lines up.
/// </summary>
public class WorktreeManagerTests {
    static (string upstream, string clone) MakeUpstreamWithSideRef(string sideRefName, out string sideCommitSha) {
        var upstream = Path.Combine(Path.GetTempPath(), "kapacitor-upstream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(upstream);

        Git(upstream, "init", "-q");
        Git(upstream, "config", "user.email", "test@example.com");
        Git(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "main.txt"), "main");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "initial");

        // Capture the default branch name; git's default has shifted from
        // master to main and varies by user config.
        var defaultBranch = GitCapture(upstream, "branch", "--show-current").Trim();

        // Create a second commit on a detached side branch and store it under
        // a custom ref so the clone can fetch it like a PR head.
        Git(upstream, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(upstream, "side.txt"), "side");
        Git(upstream, "add", "-A");
        Git(upstream, "commit", "-q", "-m", "side commit");
        sideCommitSha = GitCapture(upstream, "rev-parse", "HEAD").Trim();
        Git(upstream, "update-ref", sideRefName, sideCommitSha);
        Git(upstream, "checkout", "-q", defaultBranch);
        Git(upstream, "branch", "-D", "side");

        // Allow `git clone` of a non-bare repo over the file:// protocol.
        Git(upstream, "config", "uploadpack.allowAnySHA1InWant", "true");

        var clone = Path.Combine(Path.GetTempPath(), "kapacitor-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Git(Path.GetTempPath(), "clone", "-q", upstream, clone);

        return (upstream, clone);
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
        return proc.StandardOutput.ReadToEnd();
    }

    [Test]
    public async Task CreateAsync_WithBaseRef_WorktreeHeadMatchesFetchedCommit() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/42/head", out var sideSha);
        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone, name: "review-pr-42", baseRef: "refs/pull/42/head");

            try {
                var head = GitCapture(worktree.Path, "rev-parse", "HEAD").Trim();

                await Assert.That(head).IsEqualTo(sideSha);
                await Assert.That(worktree.Branch).IsEqualTo("capacitor/review-pr-42");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch { /* best-effort */ }
            try { Directory.Delete(clone,    true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task CreateAsync_WithoutBaseRef_StillWorks() {
        var (upstream, clone) = MakeUpstreamWithSideRef("refs/pull/1/head", out _);
        try {
            var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);
            var worktree = await manager.CreateAsync(clone);

            try {
                await Assert.That(Directory.Exists(worktree.Path)).IsTrue();
                await Assert.That(worktree.Branch).StartsWith("capacitor/");
            } finally {
                await WorktreeManager.RemoveAsync(worktree);
            }
        } finally {
            try { Directory.Delete(upstream, true); } catch { /* best-effort */ }
            try { Directory.Delete(clone,    true); } catch { /* best-effort */ }
        }
    }
}
