using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the install/remove/marker MECHANICS of <see cref="DshExtensionInstaller"/>.
/// The embedded plugin body is a documented placeholder pending dsh's real
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

    [Test]
    public async Task RegisterInCordisPatch_is_idempotent_and_preserves_user_entries() {
        using var tmp = new TempDir();
        var patch  = Path.Combine(tmp.Path, "cordis.patch.yml");
        var plugin = Path.Combine(tmp.Path, "kcap-dsh.plugin.mjs");

        // empty array base
        await File.WriteAllTextAsync(patch, "[]\n");
        await Assert.That(DshExtensionInstaller.RegisterInCordisPatch(patch, plugin)).IsTrue();
        var once = await File.ReadAllTextAsync(patch);
        await Assert.That(once.Contains("id: kcap")).IsTrue();
        await Assert.That(once.Contains("kcap-dsh.plugin.mjs")).IsTrue();
        await Assert.That(DshExtensionInstaller.IsRegisteredInCordisPatch(patch)).IsTrue();
        await Assert.That(once.Contains("[]")).IsFalse();   // [] base replaced, not appended-to

        // re-register → still exactly one managed block
        DshExtensionInstaller.RegisterInCordisPatch(patch, plugin);
        var twice = await File.ReadAllTextAsync(patch);
        await Assert.That(CountOccurrences(twice, "kcap-dsh:begin")).IsEqualTo(1);

        // unregister → block gone, empty array restored
        await Assert.That(DshExtensionInstaller.UnregisterFromCordisPatch(patch)).IsTrue();
        await Assert.That(DshExtensionInstaller.IsRegisteredInCordisPatch(patch)).IsFalse();
        await Assert.That((await File.ReadAllTextAsync(patch)).Trim()).IsEqualTo("[]");
    }

    [Test]
    public async Task RegisterInCordisPatch_preserves_existing_block_style_entries() {
        using var tmp = new TempDir();
        var patch  = Path.Combine(tmp.Path, "cordis.patch.yml");
        var plugin = Path.Combine(tmp.Path, "kcap-dsh.plugin.mjs");

        await File.WriteAllTextAsync(patch, "- id: directory-picker\n  disabled: true\n");
        DshExtensionInstaller.RegisterInCordisPatch(patch, plugin);
        var content = await File.ReadAllTextAsync(patch);
        await Assert.That(content.Contains("directory-picker")).IsTrue();   // user entry preserved
        await Assert.That(content.Contains("id: kcap")).IsTrue();

        DshExtensionInstaller.UnregisterFromCordisPatch(patch);
        var after = await File.ReadAllTextAsync(patch);
        await Assert.That(after.Contains("directory-picker")).IsTrue();     // still there after remove
        await Assert.That(after.Contains("id: kcap")).IsFalse();
    }

    static int CountOccurrences(string s, string sub) {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
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
