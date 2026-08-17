using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexHooksInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "does-not-exist", "hooks.json");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName),
            "1.2.3");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_hooks_json_has_kcap_entry_but_no_marker() {
        // Pre-marker install: hooks.json was written by an older CLI build
        // that didn't stamp a marker. The very first upgrade must still
        // refresh, otherwise stale command strings linger forever.
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 5 }] }
                ]
              }
            }
            """);
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_has_only_third_party_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_is_malformed() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, "{not json");
        await Assert.That(CodexHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CodexHooksInstaller.WriteMarker(hooksPath);
        await Assert.That(CodexHooksInstaller.ReadMarker(hooksPath))
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task ReadMarker_returns_null_when_marker_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await Assert.That(CodexHooksInstaller.ReadMarker(hooksPath)).IsNull();
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CodexHooksInstaller.WriteMarker(hooksPath);
        CodexHooksInstaller.DeleteMarker(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsFalse();

        // Idempotent — calling twice does not throw.
        CodexHooksInstaller.DeleteMarker(hooksPath);
    }
}
