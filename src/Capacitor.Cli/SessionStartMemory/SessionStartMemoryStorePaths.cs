using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryStorePaths {
    public static string DefaultRoot => PathHelpers.ConfigPath(Path.Combine("cache", "session-start-memory-v1"));

    public static string ValidateRoot(string root) {
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        if (!OperatingSystem.IsWindows()) {
            try {
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { }
        }
        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("SessionStart memory store root may not be a symlink or reparse point.");
        return full;
    }
}
