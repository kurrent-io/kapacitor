namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by the tray entry label (spec §5) and the main-window
/// grid's Repo cell (spec §8) — one helper, not duplicated presentation logic. When the path ends
/// in exactly "&lt;repoDir&gt;/.claude/worktrees/&lt;leaf&gt;" (either separator flavor, case-sensitive),
/// the generated worktree leaf is meaningless noise, so this returns just "{repoDir}" — the leaf
/// never appears in presentation; the full path (worktree leaf included) is still the tooltip, for
/// anyone who needs to tell worktrees of the same repo apart.
public static class RepoLabel {
    public static string Leaf(string? repoPath) {
        if (repoPath is null) return "—";

        var segments = repoPath.Replace('\\', '/').TrimEnd('/').Split('/');
        if (segments.Length >= 4 && segments[^3] == ".claude" && segments[^2] == "worktrees" && segments[^4].Length > 0)
            return segments[^4];

        return Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
    }
}

// StopEnabled: false while AgentActionService.StopsInFlight contains Id. Kind is the wire
// KindText spelling (agent|review|review-flow) — carried through so the Stop click handler can
// pass it to AgentActionService.RequestStop, which decides protected-ness (decision 5).
public sealed record TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled);
public sealed record TrayPauseItem(bool Enabled, bool Checked);
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent);
