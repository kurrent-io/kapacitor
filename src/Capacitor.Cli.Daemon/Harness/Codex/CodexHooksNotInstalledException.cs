using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Thrown by <see cref="CodexLauncher"/>'s Prepare preflight when neither the
/// user-scope (<c>~/.codex/hooks.json</c>) nor project-scope
/// (<c>&lt;worktree&gt;/.codex/hooks.json</c>) hooks file has a
/// <c>kcap codex-hook</c> entry for SessionStart / Stop / PermissionRequest.
/// The orchestrator catches this type and emits <see cref="LaunchFailed"/> with
/// the exception's message; the user sees an actionable instruction to run
/// <c>kcap plugin install --codex</c>.
/// </summary>
internal sealed class CodexHooksNotInstalledException(string message) : Exception(message);
