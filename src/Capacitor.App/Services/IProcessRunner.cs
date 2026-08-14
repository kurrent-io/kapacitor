namespace Capacitor.App.Services;

/// AbandonWait: an external ct cancellation abandons the WAIT only, child keeps running.
/// KillTree: an external ct cancellation kills the child (tree) and awaits its exit first.
public enum CancelMode {
    AbandonWait,
    KillTree,
}

/// Scope of the kill on internal Timeout expiry only — CancelMode's KillTree always kills the tree.
public enum TimeoutKillScope {
    Tree,
    ProcessOnly,
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed record RunOptions(
    IReadOnlyDictionary<string, string>? EnvOverlay = null, // adds/overrides; rest of env untouched
    TimeSpan? Timeout = null,                                // internal deadline: kills the tree + awaits on expiry
    CancelMode CancelMode = CancelMode.AbandonWait,
    TimeoutKillScope TimeoutKill = TimeoutKillScope.Tree);

/// Seam over process spawning so StartDaemonAsync is testable without touching a real CLI
/// binary. The production implementation wraps System.Diagnostics.Process.
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct);
}
