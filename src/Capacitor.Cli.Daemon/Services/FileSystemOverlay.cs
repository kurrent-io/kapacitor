namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Copies files from a source dir into a destination, creating directories
/// as needed but never overwriting existing files. Used by both
/// <see cref="Harness.Claude.ClaudeLauncher"/> and <see cref="Harness.Codex.CodexLauncher"/> to merge
/// vendor-specific dotfiles from the source repo into the worktree without
/// clobbering tracked content.
/// </summary>
internal static class FileSystemOverlay {
    public static void OverlayDirectory(string source, string dest) {
        var skipReparsePoints = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };

        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source, "*", skipReparsePoints)) {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            if (!File.Exists(destFile)) File.Copy(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(source, "*", skipReparsePoints)) {
            // Never recurse into a nested git working tree / repo. A dotfile overlay copies local
            // config (settings, commands, skills) — never a checked-out repo. Some setups keep whole
            // worktrees under .claude/worktrees/ (the superpowers using-git-worktrees convention); a
            // repo root can hold hundreds, and recursing in copies gigabytes into the agent worktree
            // and wedges the daemon for many minutes per launch. A ".git" entry marks one — a
            // directory for a normal repo, a pointer file for a linked worktree.
            if (Path.Exists(Path.Combine(dir, ".git"))) continue;
            OverlayDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
