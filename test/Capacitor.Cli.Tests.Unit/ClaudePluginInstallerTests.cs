using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudePluginInstallerTests {
    // ── IsEffectivelyInstalled — the destructive-action gate ─────────────────
    //
    // Resolves the enabled plugin's INSTALLED payload the way Claude loads it
    // (plugins/installed_plugins.json → installPath; directory-sourced marketplaces live
    // via known_marketplaces.json installLocation). A marker, an enabled flag alone, or a
    // marketplace SOURCE dir prove nothing about what Claude actually loads.

    static string WriteEnabledSettings(string home) {
        var settingsPath = Path.Combine(home, "settings.json");
        File.WriteAllText(settingsPath, """{ "enabledPlugins": { "kcap@kcap": true } }""");
        return settingsPath;
    }

    static void WriteInstallRecord(string home, string installPath) =>
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(home, "plugins")).FullName,
                                       "installed_plugins.json"), $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "user", "installPath": {{JsonValue.Create(installPath).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);

    [Test]
    public async Task IsEffectivelyInstalled_true_when_the_installed_cache_ships_the_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(install, ".mcp.json"), "{}");
        WriteInstallRecord(tmp.Path, install);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    /// <summary>Directory-sourced marketplaces are resolved live: the install record's cache
    /// path never materializes, and the loadable payload is the marketplace installLocation.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_true_for_directory_marketplace_with_phantom_cache_path() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")); // never created
        var location = Directory.CreateDirectory(Path.Combine(tmp.Path, "npm-kcap", "kcap")).FullName;
        File.WriteAllText(Path.Combine(location, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(tmp.Path, "plugins", "known_marketplaces.json"), $$"""
            { "kcap": { "source": { "source": "directory", "path": {{JsonValue.Create(location).ToJsonString()}} },
                        "installLocation": {{JsonValue.Create(location).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    /// <summary>
    /// The directory-marketplace exception is for LIVE resolution only. A git/github-sourced
    /// marketplace IS cached: once its installed cache payload is gone, a lingering checkout
    /// under installLocation proves nothing — accepting it would let doctor --clean delete the
    /// user's only working registrations.
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_for_git_marketplace_with_lingering_checkout_and_no_cache() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")); // cache gone
        var checkout = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins", "marketplaces", "kcap")).FullName;
        File.WriteAllText(Path.Combine(checkout, ".mcp.json"), "{}"); // stale clone still ships the payload
        File.WriteAllText(Path.Combine(tmp.Path, "plugins", "known_marketplaces.json"), $$"""
            { "kcap": { "source": { "source": "github", "repo": "kurrent-io/kcap-cli" },
                        "installLocation": {{JsonValue.Create(checkout).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>
    /// Both callers gate on the USER-scope settings.json enabled flag, so only a user-scoped
    /// v2 install entry proves that flag's payload — a project/local-scoped install belonging
    /// to some unrelated repo must not make the plugin globally "effective".
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_a_project_scoped_install_has_the_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(install, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins")).FullName,
                                       "installed_plugins.json"), $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "project", "installPath": {{JsonValue.Create(install).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>
    /// The cross-product of the two round-3 gates: a v2 record with ONLY a project-scoped
    /// install must not fall through to the directory-marketplace fallback either — that
    /// fallback only excuses a phantom cache path on an otherwise-eligible (user-scoped)
    /// record, never the absence of an eligible record.
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_for_project_only_record_even_with_directory_marketplace() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(install, ".mcp.json"), "{}");
        var checkout = Directory.CreateDirectory(Path.Combine(tmp.Path, "marketplace-src", "kcap")).FullName;
        File.WriteAllText(Path.Combine(checkout, ".mcp.json"), "{}");
        var pluginsDir = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins")).FullName;
        File.WriteAllText(Path.Combine(pluginsDir, "installed_plugins.json"), $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "project", "installPath": {{JsonValue.Create(install).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);
        File.WriteAllText(Path.Combine(pluginsDir, "known_marketplaces.json"), $$"""
            { "kcap": { "source": { "source": "directory" },
                        "installLocation": {{JsonValue.Create(checkout).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>Pre-v2 compatibility: a bare-object install record predates scopes and stays accepted.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_true_for_bare_object_install_record_with_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins", "cache", "kcap", "kcap", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(install, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(tmp.Path, "plugins")).FullName,
                                       "installed_plugins.json"), $$"""
            { "version": 1, "plugins": { "kcap@kcap":
                { "installPath": {{JsonValue.Create(install).ToJsonString()}}, "version": "1.0.0" } } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_enabled_but_no_install_record() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_the_marker_exists() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        File.WriteAllText(Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName), "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>The round-1 gap this closes: a marketplace SOURCE dir shipping .mcp.json does
    /// not prove the installed payload exists — without an install record it is not effective.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_a_marketplace_source_dir_has_the_payload() {
        using var tmp = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(tmp.Path, "source-kcap")).FullName;
        File.WriteAllText(Path.Combine(source, ".mcp.json"), "{}");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        File.WriteAllText(settingsPath, $$"""
            { "enabledPlugins": { "kcap@kcap": true },
              "extraKnownMarketplaces": { "kcap": { "source": {
                  "source": "directory", "path": {{JsonValue.Create(source).ToJsonString()}} } } } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_the_install_record_points_nowhere() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, Path.Combine(tmp.Path, "gone")); // no cache, no marketplace record
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "does-not-exist", "settings.json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(
            Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName),
            "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_enabledPlugins_has_kcap() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_marketplace_has_kcap() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_enabledPlugins_kurrent_key_present() {
        // Pre-rename installs used the "kcap@kurrent" key. The installer
        // and remover both treat it as a kcap-owned stale entry, so the
        // refresh gate must detect it too — otherwise users on a pre-marker
        // pre-rename config would never get migrated.
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kcap@kurrent": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_marketplace_kurrent_key_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kurrent": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_enabledPlugins_present() {
        // Pre-rename installs used the "kapacitor@kapacitor" key. Refresh on
        // upgrade must pick this up so the user gets migrated to the kcap
        // marketplace entry.
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_kurrent_enabledPlugins_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kurrent": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_marketplace_present() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_has_unrelated_keys_only() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "theme": "dark" }""");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_is_malformed() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{not json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settingsPath))
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        ClaudePluginInstaller.DeleteMarker(settingsPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        ClaudePluginInstaller.DeleteMarker(settingsPath);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-claude-plugin-installer-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
