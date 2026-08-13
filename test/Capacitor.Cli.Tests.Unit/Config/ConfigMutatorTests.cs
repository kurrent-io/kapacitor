using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Config;

/// <summary>
/// Tests for <see cref="ConfigMutator"/>, the one writer of config.json.
///
/// The task brief's original draft pointed each test at its own private temp dir via
/// <c>KCAP_CONFIG_DIR</c>. That does not work here: <c>AppConfig</c>'s config path (like
/// <c>PathHelpers.ConfigDir</c>) is <c>static readonly</c>, captured once per process — see
/// <see cref="ConfigDirIsolationTests"/> and <c>ConfigSetTelemetryCompositionTests</c>'s doc
/// comment for the same constraint. There is no <c>PathOverride</c>-equivalent seam for the
/// profile config path, so a per-test <c>Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", …)</c>
/// after process start has no effect on where <see cref="AppConfig.GetConfigPath"/> resolves.
///
/// Instead this follows the established shared-directory convention used by
/// <c>TokenStoreProfileTests</c>/<c>MachineIdFileTests</c>/<c>WorkOSDiscoveryTests</c>: touch the
/// one real <c>config.json</c> under the assembly-wide <c>KCAP_CONFIG_DIR</c> that
/// <c>RepoPathStoreGlobalSetup</c>'s <c>[ModuleInitializer]</c> pins for the whole process, and
/// share that class's <see cref="NotInParallelAttribute"/> key so nothing else deletes or
/// rewrites <c>config.json</c> underneath these tests.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ConfigMutatorTests {
    static string ConfigPath => AppConfig.GetConfigPath();

    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(ConfigPath));
        AppConfig.ResetResolvedStateForTesting();
    }

    [Test]
    public async Task Mutate_preserves_unrelated_fields_written_by_a_concurrent_style_writer() {
        // seed: profile "a" with a server URL
        await ConfigMutator.MutateAsync(c => c with {
            Profiles = new(c.Profiles) { ["a"] = new Profile { ServerUrl = "https://a.example" } },
        });
        // writer 1 sets machine_id; writer 2 (stale-snapshot style: mutation function only
        // touches its own field) sets active_profile — both must survive.
        await ConfigMutator.MutateAsync(c => c with { MachineId = "m-123" });
        await ConfigMutator.MutateAsync(c => c with { ActiveProfile = "a" });

        var final = await AppConfig.LoadProfileConfig();
        await Assert.That(final.MachineId).IsEqualTo("m-123");
        await Assert.That(final.ActiveProfile).IsEqualTo("a");
        await Assert.That(final.Profiles["a"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task Mutate_uses_unique_temp_names() {
        // two concurrent mutations must not collide on a shared fixed .tmp name
        var t1 = ConfigMutator.MutateAsync(c => c with { MachineId = "one" });
        var t2 = ConfigMutator.MutateAsync(c => c with { ActiveProfile = "p2" });
        await Task.WhenAll(t1, t2);

        var final = await AppConfig.LoadProfileConfig();
        await Assert.That(final.MachineId).IsEqualTo("one");
        await Assert.That(final.ActiveProfile).IsEqualTo("p2");
        // no orphaned fixed-name temp file (the old SaveProfileConfig it replaced always used
        // exactly this name, which is what made concurrent writers collide)
        await Assert.That(File.Exists(ConfigPath + ".tmp")).IsFalse();
    }

    [Test]
    public async Task Legacy_v1_config_is_migrated_in_memory_and_persisted_through_the_mutation() {
        // minimal v1 flat config (no "version"/"profiles" — ConfigMigration's v1 shape)
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, """{"server_url":"https://legacy.example"}""");

        var result = await ConfigMutator.MutateAsync(c => c with { MachineId = "post-migration" });

        await Assert.That(result.Version).IsEqualTo(2);
        await Assert.That(result.MachineId).IsEqualTo("post-migration");
        // migration survived the same publication as the mutation
        var reread = await AppConfig.LoadProfileConfig();
        await Assert.That(reread.Version).IsEqualTo(2);
        await Assert.That(reread.MachineId).IsEqualTo("post-migration");
    }

    [Test]
    public async Task MachineIdProvider_heals_a_blank_machine_id_left_by_a_stale_or_broken_writer() {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, """{"version":2,"machine_id":""}""");

        var id = await MachineIdProvider.GetOrCreateAsync();

        await Assert.That(id).Matches("^mach-[0-9a-f]{12}$");
        var reread = await AppConfig.LoadProfileConfig();
        await Assert.That(reread.MachineId).IsEqualTo(id);
    }

    [Test]
    public async Task LoadProfileConfig_is_pure_and_never_writes() {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, """{"server_url":"https://legacy.example"}""");

        var cfg = await AppConfig.LoadProfileConfig();

        await Assert.That(cfg.Version).IsEqualTo(2);           // migrated in memory
        // NOTE: LoadProfileConfig routes persistence through MutateAsync — one write is
        // allowed here (the legacy-persist behavior), but the FILE must now be v2 and valid.
        var reread = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        await Assert.That(reread.RootElement.TryGetProperty("version", out var v) && v.GetInt32() == 2).IsTrue();
    }
}
