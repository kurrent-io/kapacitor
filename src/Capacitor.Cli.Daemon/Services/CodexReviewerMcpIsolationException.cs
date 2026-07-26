namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Thrown by <see cref="CodexLauncher"/> while building a review-flow reviewer's launch args
/// when the reviewer's inherited MCP servers cannot be authoritatively enumerated (so we cannot
/// prove every flow-capable server is disabled). Enumeration is the recursion guard's foundation:
/// if <c>codex mcp list --json</c> can't be run or parsed, we do NOT fall back to disabling
/// nothing (which would let a hand-registered flow-starting server through) — we fail the launch
/// closed. The orchestrator catches this type and emits <see cref="LaunchFailed"/> with the
/// exception's message + the same worktree/token cleanup as
/// <see cref="CodexHooksNotInstalledException"/>.
/// </summary>
internal sealed class CodexReviewerMcpIsolationException(string message) : Exception(message);
