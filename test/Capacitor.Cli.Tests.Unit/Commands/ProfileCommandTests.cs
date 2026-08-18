using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>ProfileCommand.AddProfile</c>/<c>RemoveProfile</c>'s actual write now goes through
/// <see cref="ConfigMutator"/>, which always targets
/// <see cref="AppConfig.GetConfigPath"/> — a <c>static readonly</c> path pinned once per
/// process (see <c>ConfigDirIsolationTests</c>, <c>ConfigMutatorTests</c>). So these tests can
/// no longer point <c>configPath</c> at a private per-test temp dir and expect the mutator to
/// honor it: they seed and assert against the one shared <c>KCAP_CONFIG_DIR</c> the whole
/// assembly uses, sharing <c>TokenStoreProfileTests</c>'s <see cref="NotInParallelAttribute"/>
/// key like every other config.json-touching test class.
/// </summary>
[NotInParallel("TokenStoreProfileTests")]
public class ProfileCommandTests {
    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(AppConfig.GetConfigPath()));
        AppConfig.ResetResolvedStateForTesting();
    }

    [Test]
    public async Task AddProfile_CreatesNewProfile() {
        var configPath = AppConfig.GetConfigPath();

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "https://contoso.kcap.io",
            ["github.com/contoso/*"]
        );

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).ContainsKey("contoso");
        await Assert.That(config.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
        await Assert.That(config.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
    }

    [Test]
    public async Task RemoveProfile_DeletesProfile() {
        var configPath = AppConfig.GetConfigPath();

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "contoso");

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).DoesNotContainKey("contoso");
    }

    [Test]
    public async Task AddProfile_SchemeLessInput_AddsHttpsAndStoresNormalizedUrl() {
        var configPath = AppConfig.GetConfigPath();

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        // skipProbe defaults to true → no network, falls back to loopback heuristic.
        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "contoso.kcap.io", remotes: []);

        await Assert.That(result).IsEqualTo(0);

        var saved = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(configPath),
            ProfileConfigJsonContextIndented.Default.ProfileConfig)!;

        await Assert.That(saved.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
    }

    [Test]
    public async Task RemoveProfile_CannotRemoveDefault() {
        var configPath = AppConfig.GetConfigPath();

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "default");

        await Assert.That(result).IsEqualTo(1);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(config.Profiles).ContainsKey("default");
    }
}
