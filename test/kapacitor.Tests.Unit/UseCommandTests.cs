using System.Text.Json;
using kapacitor.Config;
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class UseCommandTests {
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

    [Test]
    public async Task Use_InRepo_SetsProfileBinding() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: "/repos/my-project", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ProfileBindings["/repos/my-project"]).IsEqualTo("contoso");
        await Assert.That(config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Use_Global_SetsActiveProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ActiveProfile).IsEqualTo("contoso");
    }

    [Test]
    public async Task Use_Save_WritesRepoConfig() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(repoRoot);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: repoRoot, global: false, save: true, savePath: repoRoot);

        await Assert.That(result).IsEqualTo(0);

        var repoConfigPath = Path.Combine(repoRoot, ".kapacitor.json");
        await Assert.That(File.Exists(repoConfigPath)).IsTrue();

        var repoConfigJson = await File.ReadAllTextAsync(repoConfigPath);
        var repoConfig = JsonSerializer.Deserialize(repoConfigJson, RepoConfigJsonContext.Default.RepoConfig)!;

        await Assert.That(repoConfig.Profile).IsEqualTo("contoso");
        await Assert.That(repoConfig.ServerUrl).IsEqualTo("https://contoso.com");
    }

    [Test]
    public async Task Use_UnknownProfile_ReturnsError() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "nonexistent", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
    }
}
