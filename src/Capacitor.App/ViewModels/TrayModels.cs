using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

/// Last path segment of a repo path, shared by every surface that names a repository. The status
/// wire says which path is a worktree, so a path's shape is never read as evidence of one.
public static class RepoLabel {
    public static string Leaf(string? repoPath) => repoPath is null ? "—" : PlatformPaths.Leaf(repoPath);
}

/// The checkout a session is presented under, shared by the rail's grouping and the workspace
/// subtitle so the two cannot disagree: the checkout a reviewer borrowed comes first, so a
/// snapshot reviewer sits beside the session it reviews rather than under its private copy.
public static class CheckoutLabel {
    /// Null from an older daemon, whose RepoPath is the checkout.
    public static string? CheckoutPathFor(AgentStatusDto dto) => dto.BorrowedFrom ?? dto.WorktreePath;

    public static bool IsMain(string checkout, string repoRoot) =>
        PlatformPaths.Comparer.Equals(
            Path.TrimEndingDirectorySeparator(checkout), Path.TrimEndingDirectorySeparator(repoRoot));

    public static string Format(string checkout, string repoRoot) =>
        IsMain(checkout, repoRoot) ? "main checkout" : PlatformPaths.Leaf(checkout);
}

/// The path primitives every surface shares: one platform comparison rule and one leaf
/// expression, never a per-view copy.
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
