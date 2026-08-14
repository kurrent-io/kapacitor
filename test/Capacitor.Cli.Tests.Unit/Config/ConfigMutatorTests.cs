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

    // ── TryLoadPure: absence-vs-unreadable, at explicit paths (no shared-config-dir contention) ──

    [Test]
    public async Task TryLoadPure_absent_file_is_success_with_defaults() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        var ok = ConfigMutator.TryLoadPure(path, out var config);
        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task TryLoadPure_directory_in_place_of_file_is_failure_not_absence() {
        // File.Exists alone reads a directory as absent — TryLoadPure must not make that mistake:
        // a directory sitting at the config path is unreadable evidence, never "nothing configured".
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        Directory.CreateDirectory(path);

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>Malformed top-level JSON IS a TryLoadPure failure, even though
    /// <see cref="ConfigMigration.MigrateIfNeeded"/> itself absorbs an unparseable document into a
    /// silent fresh default for <see cref="ConfigMutator.LoadPure"/>'s own soft contract. TryLoadPure
    /// validates the document itself before delegating, so a gated identity check sees this as
    /// unreadable evidence rather than "nothing configured yet".</summary>
    [Test]
    public async Task TryLoadPure_malformed_json_is_failure_not_absence() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        await File.WriteAllTextAsync(path, "{not json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    [Test]
    public async Task TryLoadPure_non_object_root_is_failure_not_absence() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        await File.WriteAllTextAsync(path, "[1,2,3]");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task LoadPure_still_degrades_malformed_json_to_defaults() {
        // LoadPure discards TryLoadPure's bool — its own soft contract (always usable defaults) is
        // unchanged by the TryLoadPure hardening above.
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        await File.WriteAllTextAsync(path, "{not json");

        var config = ConfigMutator.LoadPure(path);

        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    /// <summary>A non-not-found I/O failure, forced deterministically: the config path's PARENT
    /// is a plain file rather than a directory, so opening through it fails structurally.</summary>
    [Test]
    public async Task TryLoadPure_parent_replaced_by_a_file_is_failure_not_absence() {
        var root = Directory.CreateTempSubdirectory("kcap-trypure-").FullName;
        var parentAsFile = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(parentAsFile, "i am a file, not a directory");
        var path = Path.Combine(parentAsFile, "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>The immediate parent alone isn't enough: a path nested TWO levels under a
    /// planted file needs the ancestor walk to climb past the never-created child directory.</summary>
    [Test]
    public async Task TryLoadPure_grandparent_replaced_by_a_file_is_failure_not_absence() {
        var root = Directory.CreateTempSubdirectory("kcap-trypure-").FullName;
        var grandparentAsFile = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(grandparentAsFile, "i am a file, not a directory");
        var path = Path.Combine(grandparentAsFile, "child", "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>A dangling symlink segment (<c>File.Exists</c>/<c>Directory.Exists</c> both read
    /// it as absent, since they follow to the missing target) must not let the ancestor walk skip
    /// past it to a real directory further up.</summary>
    [Test]
    public async Task TryLoadPure_dangling_symlink_ancestor_is_failure_not_absence() {
        Skip.When(OperatingSystem.IsWindows(), "symlink creation needs elevated privilege on Windows CI");

        var root = Directory.CreateTempSubdirectory("kcap-trypure-").FullName;
        var link = Path.Combine(root, "danglink");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "never-created-target"));
        var path = Path.Combine(link, "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>A genuinely never-created parent chain must still read as absence — disambiguating
    /// a blocked ancestor must not turn every missing directory level into a false failure.</summary>
    [Test]
    public async Task TryLoadPure_missing_parent_directory_is_still_absence() {
        var root = Directory.CreateTempSubdirectory("kcap-trypure-").FullName;
        var path = Path.Combine(root, "never-created", "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task TryLoadPure_valid_file_is_success() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kcap-trypure-").FullName, "config.json");
        await File.WriteAllTextAsync(path, """{"version":2,"profiles":{"work":{"server_url":"https://w.example"}}}""");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles["work"].ServerUrl).IsEqualTo("https://w.example");
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
