namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by the tray entry label (spec §5) and the main-window
/// grid's Repo cell (spec §8) — one helper, not duplicated presentation logic.
public static class RepoLabel {
    public static string Leaf(string? repoPath) =>
        repoPath is null ? "—" : Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
}

public sealed record TrayAgentEntry(string Id, string Label, bool StopEnabled); // StopEnabled: false while AgentActionService.StopsInFlight contains Id
public sealed record TrayPauseItem(bool Enabled, bool Checked);
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause);
