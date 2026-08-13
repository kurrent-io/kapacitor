using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Config;

/// <summary>
/// Pins the AI-1655 Task 3 outcome: every CLI command that writes <c>config.json</c> now
/// goes through <see cref="ConfigMutator"/> — there is no more per-command fixed-temp writer
/// to race against. The compile-time half of that guarantee is that <c>AppConfig.SaveProfileConfig</c>
/// no longer exists (removed in Task 2) and the private atomic-save helpers in
/// <c>ProfileCommand</c>/<c>UseCommand</c> are gone too (removed here); this test pins the
/// runtime half: 16 concurrent field-scoped mutations, styled after 16 different commands each
/// touching their own profile key, all survive instead of colliding on a shared fixed
/// <c>.tmp</c> name.
///
/// Follows <c>ConfigMutatorTests</c>'s established convention rather than the task brief's
/// original draft (a per-test <c>Directory.CreateTempSubdirectory</c> + <c>KCAP_CONFIG_DIR</c>
/// override): <c>PathHelpers.ConfigDir</c> is <c>static readonly</c>, captured once for the
/// whole process by <c>RepoPathStoreGlobalSetup</c>'s <c>[ModuleInitializer]</c>, so a
/// per-test env var set after process start has no effect on where
/// <see cref="AppConfig.GetConfigPath"/> resolves. See <c>ConfigMutatorTests</c>'s doc comment
/// for the same constraint.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ConfigWriterMigrationTests {
    static string ConfigPath => AppConfig.GetConfigPath();

    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(ConfigPath));
        AppConfig.ResetResolvedStateForTesting();
    }

    [Test]
    public async Task No_writer_bypasses_the_mutator() {
        var writers = Enumerable.Range(0, 16).Select(i => ConfigMutator.MutateAsync(c => c with {
            Profiles = new(c.Profiles) { [$"p{i}"] = new Profile { ServerUrl = $"https://p{i}.example" } },
        }));
        await Task.WhenAll(writers);

        var final = await AppConfig.LoadProfileConfig();
        for (var i = 0; i < 16; i++)
            await Assert.That(final.Profiles.ContainsKey($"p{i}")).IsTrue();
    }
}
