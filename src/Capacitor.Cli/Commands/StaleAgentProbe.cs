using System.Diagnostics;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The live wiring behind <see cref="StaleAgentDetector"/> — the parts that touch real processes,
/// kept apart from the rule so the rule is testable without them.
/// </summary>
/// <remarks>
/// Matching is by process name, which is what the platform makes cheap and is exact for a real binary
/// like <c>kiro-cli</c>. It is weaker for an agent launched through a node shim, whose process may be
/// named after the runtime rather than itself — that direction under-reports, which is the one to
/// prefer: a session wrongly told it is uncaptured sends someone to kill a conversation for nothing.
/// </remarks>
public static class StaleAgentProbe {
    /// <summary>
    /// Never throws and never reports on doubt: this is an advisory line after a successful install,
    /// so failing one, or guessing, would both be worse outcomes than silence.
    /// </summary>
    public static IReadOnlyList<StaleAgentProcess> Find(IEnumerable<StaleAgentTarget> targets) {
        try {
            return StaleAgentDetector.Find(targets, RunningPids, ProcessHelpers.GetProcessCwd);
        } catch {
            return [];
        }
    }

    static IEnumerable<int> RunningPids(string processName) {
        Process[] found;

        try { found = Process.GetProcessesByName(processName); } catch { return []; }

        try {
            return [.. found.Select(p => p.Id)];
        } finally {
            foreach (var process in found) process.Dispose();
        }
    }
}
