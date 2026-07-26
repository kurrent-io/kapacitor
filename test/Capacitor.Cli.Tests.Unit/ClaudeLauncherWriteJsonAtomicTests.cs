using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeLauncherWriteJsonAtomicTests {
    // Regression: with CLAUDE_CONFIG_DIR set, the user-global .claude.json lives at
    // $CLAUDE_CONFIG_DIR/.claude.json, and that directory may not exist yet (fresh /
    // freshly-relocated config). WriteJsonAtomic must create the parent dir, or the
    // trust write throws DirectoryNotFoundException and agents re-prompt for trust.
    [Test]
    public async Task WriteJsonAtomic_creates_missing_parent_directory() {
        var root = Directory.CreateTempSubdirectory("kcap-wja-test-");
        try {
            var path = Path.Combine(root.FullName, "fresh-config-dir", ".claude.json");
            var node = new JsonObject { ["projects"] = new JsonObject() };

            ClaudeLauncher.WriteJsonAtomic(path, node);

            await Assert.That(File.Exists(path)).IsTrue();
        } finally {
            root.Delete(recursive: true);
        }
    }

    // settings.json may hold secrets under `env`, so an atomic temp+rename must NOT relax its mode:
    // a fresh temp created under the umask (0644) would silently widen a 0600 target after the rename.
    [Test]
    public async Task WriteJsonAtomic_preserves_an_existing_owner_only_mode() {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("kcap-wja-perms-");
        try {
            var path = Path.Combine(root.FullName, "settings.json");
            await File.WriteAllTextAsync(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            ClaudeLauncher.WriteJsonAtomic(path, new JsonObject { ["k"] = "v" });

            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } finally {
            root.Delete(recursive: true);
        }
    }

    // A brand-new settings file is created owner-only (0600) rather than inheriting the umask.
    [Test]
    public async Task WriteJsonAtomic_creates_a_new_file_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("kcap-wja-new-");
        try {
            var path = Path.Combine(root.FullName, "settings.json");

            ClaudeLauncher.WriteJsonAtomic(path, new JsonObject { ["k"] = "v" });

            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } finally {
            root.Delete(recursive: true);
        }
    }
}
