using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UseCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Use_InRepo_SetsProfileBinding() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root).SetProfile("contoso", repoPath: "/repos/my-project", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ProfileBindings["/repos/my-project"]).IsEqualTo("contoso");
        await Assert.That(config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Use_Global_SetsActiveProfile() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root).SetProfile("contoso", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ActiveProfile).IsEqualTo("contoso");
    }

    [Test]
    public async Task Use_Save_WritesRepoConfig() {
        var configPath = AppConfig.GetConfigPath(Config.Root);
        var repoRoot   = Config.CreateDir("repo");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root).SetProfile("contoso", repoPath: repoRoot, global: false, save: true, savePath: repoRoot);

        await Assert.That(result).IsEqualTo(0);

        var repoConfigPath = repoRoot.PathTo(".kcap.json");
        await Assert.That(File.Exists(repoConfigPath)).IsTrue();

        var repoConfigJson = await File.ReadAllTextAsync(repoConfigPath);
        var repoConfig = JsonSerializer.Deserialize(repoConfigJson, RepoConfigJsonContext.Default.RepoConfig)!;

        await Assert.That(repoConfig.Profile).IsEqualTo("contoso");
        await Assert.That(repoConfig.ServerUrl).IsEqualTo("https://contoso.com");
    }

    [Test]
    public async Task Use_UnknownProfile_ReturnsError() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new UseCommand(Config.Root).SetProfile("nonexistent", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
    }
}
