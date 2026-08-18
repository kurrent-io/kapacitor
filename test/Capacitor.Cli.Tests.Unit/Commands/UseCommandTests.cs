using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>UseCommand.SetProfile</c>'s actual write now goes through <see cref="ConfigMutator"/>,
/// which always targets <see cref="AppConfig.GetConfigPath"/> — a
/// <c>static readonly</c> path pinned once per process (see <c>ConfigDirIsolationTests</c>,
/// <c>ConfigMutatorTests</c>). So these tests can no longer point <c>configPath</c> at a
/// private per-test temp dir and expect the mutator to honor it: they seed and assert against
/// the one shared <c>KCAP_CONFIG_DIR</c> the whole assembly uses, sharing
/// <c>TokenStoreProfileTests</c>'s <see cref="NotInParallelAttribute"/> key like every other
/// config.json-touching test class.
/// </summary>
[NotInParallel("TokenStoreProfileTests")]
public class UseCommandTests {
    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(AppConfig.GetConfigPath()));
        AppConfig.ResetResolvedStateForTesting();
    }

    [Test]
    public async Task Use_InRepo_SetsProfileBinding() {
        var configPath = AppConfig.GetConfigPath();

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
        var configPath = AppConfig.GetConfigPath();

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
        var configPath = AppConfig.GetConfigPath();
        using var tmp = new TempDir();
        var repoRoot = tmp.PathTo("repo");
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

        var repoConfigPath = Path.Combine(repoRoot, ".kcap.json");
        await Assert.That(File.Exists(repoConfigPath)).IsTrue();

        var repoConfigJson = await File.ReadAllTextAsync(repoConfigPath);
        var repoConfig = JsonSerializer.Deserialize(repoConfigJson, RepoConfigJsonContext.Default.RepoConfig)!;

        await Assert.That(repoConfig.Profile).IsEqualTo("contoso");
        await Assert.That(repoConfig.ServerUrl).IsEqualTo("https://contoso.com");
    }

    [Test]
    public async Task Use_UnknownProfile_ReturnsError() {
        var configPath = AppConfig.GetConfigPath();

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
