using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

/// <summary>
/// Covers the Pi extension installer: kcap.ts write/remove + the
/// version-marker helpers. Pi has no hooks.json — kcap ships a TypeScript
/// extension Pi auto-loads from <c>~/.pi/agent/extensions/</c>.
/// </summary>
public class PiExtensionInstallerTests {
    [Test]
    public async Task install_writes_extension_and_marker() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap.ts");

        var ok = PiExtensionInstaller.Install(path);
        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();

        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content.Contains("export default function")).IsTrue();
        await Assert.That(content.Contains("session_start")).IsTrue();
        await Assert.That(content.Contains("session_shutdown")).IsTrue();
        await Assert.That(content.Contains("--pi")).IsTrue();

        await Assert.That(PiExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(PiExtensionInstaller.ReadMarker(path)).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task is_installed_true_when_only_marker_present() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap.ts");
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        // Marker present but no kcap.ts (user deleted the file, kept the dir).
        await File.WriteAllTextAsync(Path.Combine(dir, PiExtensionInstaller.MarkerFileName), "9.9.9");

        await Assert.That(PiExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task remove_deletes_extension_and_marker() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap.ts");
        PiExtensionInstaller.Install(path);

        var removed = PiExtensionInstaller.Remove(path);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(PiExtensionInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task remove_returns_false_when_absent() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap.ts");

        await Assert.That(PiExtensionInstaller.Remove(path)).IsFalse();
    }
}
