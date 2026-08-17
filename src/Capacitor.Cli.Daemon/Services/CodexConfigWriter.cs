using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Daemon-facing wrapper around <see cref="CodexConfigToml.TrustWorktree(string, out System.Exception?, string?)"/> that
/// pre-trusts a worktree in <c>~/.codex/config.toml</c> so Codex doesn't prompt on
/// every hook execution. The read-modify-write plumbing lives in Core; this layer
/// only adds the daemon's structured logging on failure.
/// </summary>
internal static class CodexConfigWriter {
    public static void TrustWorktree(string worktreePath, ILogger logger) {
        if (CodexConfigToml.TrustWorktree(worktreePath, out var error) == CodexConfigToml.Change.Failed)
            logger.LogWarning(
                error,
                "Failed to read/write {Path}; Codex worktree pre-trust not persisted",
                Path.Combine(CodexPaths.Home(), "config.toml"));
    }
}
