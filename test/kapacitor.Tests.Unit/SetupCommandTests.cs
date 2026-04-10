using System.Text.Json.Nodes;
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class SetupCommandTests {
    [Test]
    public async Task InstallPlugin_CreatesNewSettingsFile() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       marketplace  = "/opt/kapacitor";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kapacitor"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);

        await Assert.That(root["enabledPlugins"]?["kapacitor@kapacitor"]?.GetValue<bool>())
            .IsEqualTo(true);
    }

    [Test]
    public async Task InstallPlugin_PreservesExistingSettings() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       marketplace  = "/opt/kapacitor";

        // Pre-populate with existing settings
        File.WriteAllText(settingsPath, """
            {
              "permissions": { "allow": ["Bash"] },
              "enabledPlugins": { "other-plugin@foo": true }
            }
            """);

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        // Original settings preserved
        await Assert.That(root["permissions"]?["allow"]?[0]?.GetValue<string>())
            .IsEqualTo("Bash");

        await Assert.That(root["enabledPlugins"]?["other-plugin@foo"]?.GetValue<bool>())
            .IsEqualTo(true);

        // Plugin added
        await Assert.That(root["enabledPlugins"]?["kapacitor@kapacitor"]?.GetValue<bool>())
            .IsEqualTo(true);

        await Assert.That(root["extraKnownMarketplaces"]?["kapacitor"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);
    }

    [Test]
    public async Task InstallPlugin_UpdatesExistingMarketplacePath() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       newPath      = "/new/path";

        // Pre-populate with old marketplace path
        File.WriteAllText(settingsPath, """
            {
              "extraKnownMarketplaces": {
                "kurrent": { "source": { "source": "directory", "path": "/old/path" } }
              },
              "enabledPlugins": { "kapacitor@kapacitor": true }
            }
            """);

        var result = SetupCommand.InstallPlugin(settingsPath, newPath);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kapacitor"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(newPath);
    }

    [Test]
    public async Task InstallPlugin_CreatesIntermediateDirectories() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, ".claude", "nested", "settings.json");
        var       marketplace  = "/opt/kapacitor";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();
        await Assert.That(File.Exists(settingsPath)).IsTrue();
    }

    [Test]
    public async Task InstallPlugin_MalformedJson_StartsFromScratch() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       marketplace  = "/opt/kapacitor";

        File.WriteAllText(settingsPath, "not json {{{");

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        await Assert.That(root["enabledPlugins"]?["kapacitor@kapacitor"]?.GetValue<bool>())
            .IsEqualTo(true);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kapacitor-test-" + Guid.NewGuid().ToString("N")[..8]
        );

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
