using System.ComponentModel;
using System.Diagnostics;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Creates disposable git repositories and runs git for the AgentOrchestrator suite.
/// </summary>
internal static class GitRepoHarness {
    internal static (string repoPath, Action cleanup) CreateGitRepo() {
        // Atomic and unique by OS guarantee. A hand-rolled "prefix + 8 hex chars of a GUID" path is
        // 32 bits across 59 call sites, and since CreateDirectory is idempotent a collision silently
        // SHARES a directory — after which either test's cleanup() recursively deletes the other's
        // repo. Also closes the window between choosing a name and owning it.
        var tmp = new TempDir();

        Git(tmp.Path, "init", "-q");
        Git(tmp.Path, "config", "user.email", "test@example.com");
        Git(tmp.Path, "config", "user.name", "Test");
        tmp.CreateFile("README.md", "test");
        Git(tmp.Path, "add", "-A");
        Git(tmp.Path, "commit", "-q", "-m", "initial");

        return (tmp.Path, tmp.Dispose);
    }

    internal static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        Process proc;

        try {
            proc = Process.Start(psi)!;
        } catch (Win32Exception ex) {
            // On Unix this spawn fails with the SAME ENOENT for two unrelated causes — (1) the
            // working directory does not exist, (2) the executable was not found — and .NET
            // interpolates the working directory into the message either way. A CI log therefore
            // cannot say which happened. Capture both facts here so the next occurrence diagnoses
            // itself.
            var cwdExists = Directory.Exists(cwd);
            var gitProbe  = ProbeGitStartable();
            var resolved  = BinaryProbe.FromEnvironment().Resolve("git");   // shared helper: PATHEXT + Unix exec bit

            throw new InvalidOperationException(
                $"Failed to start 'git {string.Join(' ', args)}'. " +
                $"WorkingDirectory '{cwd}' exists: {cwdExists}. " +
                $"'git' startable from a known-good directory: {gitProbe}. " +
                $"'git' resolves to: {resolved ?? "NOT FOUND"}. " +
                $"PATH={Environment.GetEnvironmentVariable("PATH")}",
                ex);
        }

        using (proc) {
            // Drain BOTH redirected streams before waiting. Redirecting a stream and never reading
            // it risks the child blocking forever once the pipe buffer fills, which would turn a
            // test failure into a hung run — the worst outcome, since a hang produces no report.
            // These commands are quiet enough that it has not bitten, but the shape is the bug.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            var err = stderr.GetAwaiter().GetResult();
            _       = stdout.GetAwaiter().GetResult();

            if (proc.ExitCode != 0) {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
            }
        }
    }

    /// <summary>
    /// Answers "could this process start git at all?" by ASKING THE OS — spawning `git --version`
    /// from a directory known to exist — rather than modelling executable resolution, which cannot
    /// be done correctly here: `Process.Start` with UseShellExecute=false (forced by redirecting
    /// streams) will not run a .cmd/.bat shim even though PATHEXT lists it, and a Unix execute bit
    /// says nothing about EFFECTIVE permission or whether each PATH directory is traversable.
    ///
    /// Authoritative because it uses the same mechanism that just failed, and it splits the ENOENT
    /// ambiguity: startable means the fault was the working directory, not startable means it was
    /// the executable. `BinaryProbe.Resolve` reports WHICH git alongside it.
    /// </summary>
    internal static string ProbeGitStartable() {
        // Bounded so the diagnostic can never become the problem. This runs on a FAILURE path in a
        // suite CI executes serially, so an unbounded wait would wedge the entire run and produce no
        // report at all — strictly worse than the error it is trying to explain.
        const int probeTimeoutMs = 10_000;

        // "NO" must mean exactly one thing: git could not be STARTED. Post-start failures (stream
        // reads, the wait, ExitCode, disposal) keep the YES, or they conflate the two facts this
        // probe exists to separate. It must also NEVER throw — it runs inside the
        // `catch (Win32Exception)` above, so an escaping exception would REPLACE the message.
        Process? probe = null;

        try {
            try {
                probe = Process.Start(new ProcessStartInfo("git", "--version") {
                    WorkingDirectory       = Path.GetTempPath(), // known to exist; not the suspect dir
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                });
            } catch (Exception startEx) {
                return $"NO — {startEx.GetType().Name}: {startEx.Message}";
            }

            if (probe is null) return "NO — Process.Start returned null";

            // Past here startability is PROVEN. Everything below is a separate fact and must keep
            // the YES, however it goes wrong.
            try {
                var versionTask = probe.StandardOutput.ReadToEndAsync();
                var errTask     = probe.StandardError.ReadToEndAsync();

                if (!probe.WaitForExit(probeTimeoutMs)) {
                    try { probe.Kill(entireProcessTree: true); } catch { /* best effort */ }

                    return $"YES (startable; probe did not exit within {probeTimeoutMs}ms, killed)";
                }

                var version = versionTask.GetAwaiter().GetResult().Trim();
                var err     = errTask.GetAwaiter().GetResult().Trim();

                return probe.ExitCode == 0
                    ? $"YES ({version})"
                    : $"YES (startable; --version exited {probe.ExitCode}: {err})";
            } catch (Exception afterStartEx) {
                return $"YES (startable; probe failed after starting — " +
                       $"{afterStartEx.GetType().Name}: {afterStartEx.Message})";
            }
        } finally {
            // Disposal must not be able to change the verdict or escape.
            try { probe?.Dispose(); } catch { /* best effort */ }
        }
    }
}
