using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// Pins the config-writer migration outcome: every CLI command that writes <c>config.json</c> now
/// goes through <see cref="ConfigMutator"/> — there is no more per-command fixed-temp writer
/// to race against. The compile-time half of that guarantee is that <c>AppConfig.SaveProfileConfig</c>
/// no longer exists (removed earlier in the migration) and the private atomic-save helpers in
/// <c>ProfileCommand</c>/<c>UseCommand</c> are gone too (removed here); this test pins the
/// runtime half: 16 concurrent field-scoped mutations, styled after 16 different commands each
/// touching their own profile key, all survive instead of colliding on a shared fixed
/// <c>.tmp</c> name.
/// </summary>
public class ConfigWriterMigrationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task No_writer_bypasses_the_mutator() {
        var writers = Enumerable.Range(0, 16).Select(i => ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new(c.Profiles) { [$"p{i}"] = new Profile { ServerUrl = $"https://p{i}.example" } },
        }));
        await Task.WhenAll(writers);

        var final = await AppConfig.LoadProfileConfig(Config.Root);
        for (var i = 0; i < 16; i++)
            await Assert.That(final.Profiles.ContainsKey($"p{i}")).IsTrue();
    }
}
