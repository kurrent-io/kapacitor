using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorHooksInstallerTests {
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "does-not-exist", "hooks.json");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, CursorHooksInstaller.MarkerFileName), "1.2.3");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_hooks_json_has_kcap_entry_but_no_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_has_only_third_party_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"/usr/local/bin/other"}]}}
            """);
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_hooks_json_is_malformed() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, "{not json");
        await Assert.That(CursorHooksInstaller.IsInstalled(hooksPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        await Assert.That(CursorHooksInstaller.ReadMarker(hooksPath))
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task ReadMarker_returns_null_when_marker_missing() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await Assert.That(CursorHooksInstaller.ReadMarker(hooksPath)).IsNull();
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        CursorHooksInstaller.DeleteMarker(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CursorHooksInstaller.MarkerFileName))).IsFalse();
        CursorHooksInstaller.DeleteMarker(hooksPath); // idempotent
    }
}
