using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the install/remove/marker MECHANICS of <see cref="DshExtensionInstaller"/>
/// (AI-2020). The embedded plugin body is a documented placeholder pending dsh's real
/// plugin API, but the file/marker lifecycle is final and asserted here.
/// </summary>
public class DshExtensionInstallerTests {
    [Test]
    public async Task Install_writes_plugin_and_marker_then_Remove_clears_both() {
        using var tmp = new TempDir();
        var pluginPath = Path.Combine(tmp.Path, "plugins", "kcap.dsh.js");

        await Assert.That(DshExtensionInstaller.IsInstalled(pluginPath)).IsFalse();

        var installed = DshExtensionInstaller.Install(pluginPath);
        await Assert.That(installed).IsTrue();
        await Assert.That(File.Exists(pluginPath)).IsTrue();
        await Assert.That(DshExtensionInstaller.IsInstalled(pluginPath)).IsTrue();
        await Assert.That(DshExtensionInstaller.ReadMarker(pluginPath)).IsNotNull();

        var removed = DshExtensionInstaller.Remove(pluginPath);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(pluginPath)).IsFalse();
        await Assert.That(DshExtensionInstaller.IsInstalled(pluginPath)).IsFalse();  // marker also cleared
    }

    [Test]
    public async Task IsInstalled_true_when_only_marker_present() {
        using var tmp = new TempDir();
        var pluginPath = Path.Combine(tmp.Path, "plugins", "kcap.dsh.js");

        DshExtensionInstaller.Install(pluginPath);
        File.Delete(pluginPath);  // user removed the plugin but kept the dir/marker

        await Assert.That(DshExtensionInstaller.IsInstalled(pluginPath)).IsTrue();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-dsh-installer-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
