namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by every surface that names a repository. The status
/// wire says which path is a worktree, so a path's shape is never read as evidence of one.
public static class RepoLabel {
    public static string Leaf(string? repoPath) => repoPath is null ? "—" : PlatformPaths.Leaf(repoPath);
}

/// The path primitives every surface shares, so the platform rule and the leaf expression exist
/// once (they were drifting into per-VM copies).
public static class PlatformPaths {
    /// Repo paths compare the way the filesystem underneath them does: case-insensitively on
    /// Windows and macOS, case-sensitively on Linux where two checkouts differing only in case
    /// are genuinely different repositories.
    public static readonly StringComparer Comparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// The path's own last segment, verbatim.
    public static string Leaf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
}

// StopEnabled: false while AgentActionService.StopsInFlight contains Id. Kind is the wire
// KindText spelling (agent|review|review-flow) — carried through so the Stop click handler can
// pass it to AgentActionService.RequestStop, which decides protected-ness (decision 5).
public sealed record TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled);
public sealed record TrayPauseItem(bool Enabled, bool Checked);
// ShimInstallVisible (spec §5): "Install command-line tool…" tray-item visibility — trailing
// with a default so every existing positional/object-initializer call site stays valid.
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent, bool ShimInstallVisible = false);
