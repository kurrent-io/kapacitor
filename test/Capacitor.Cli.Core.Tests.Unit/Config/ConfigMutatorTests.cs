using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// Tests for <see cref="ConfigMutator"/>, the one writer of config.json. Each test owns a root, so
/// the real <c>config.json</c> under the assembly-wide <c>KCAP_CONFIG_DIR</c> is not involved and
/// neither is the exclusion every writer of it needed.
/// </summary>
public class ConfigMutatorTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Mutate_preserves_unrelated_fields_written_by_a_concurrent_style_writer() {
        // seed: profile "a" with a server URL
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new(c.Profiles) { ["a"] = new Profile { ServerUrl = "https://a.example" } },
        });
        // writer 1 sets machine_id; writer 2 (stale-snapshot style: mutation function only
        // touches its own field) sets active_profile — both must survive.
        await ConfigMutator.MutateAsync(Config.Root, c => c with { MachineId = "m-123" });
        await ConfigMutator.MutateAsync(Config.Root, c => c with { ActiveProfile = "a" });

        var final = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(final.MachineId).IsEqualTo("m-123");
        await Assert.That(final.ActiveProfile).IsEqualTo("a");
        await Assert.That(final.Profiles["a"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task Mutate_uses_unique_temp_names() {
        // two concurrent mutations must not collide on a shared fixed .tmp name
        var t1 = ConfigMutator.MutateAsync(Config.Root, c => c with { MachineId = "one" });
        var t2 = ConfigMutator.MutateAsync(Config.Root, c => c with { ActiveProfile = "p2" });
        await Task.WhenAll(t1, t2);

        var final = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(final.MachineId).IsEqualTo("one");
        await Assert.That(final.ActiveProfile).IsEqualTo("p2");
        // no orphaned fixed-name temp file (the old SaveProfileConfig it replaced always used
        // exactly this name, which is what made concurrent writers collide)
        await Assert.That(File.Exists(AppConfig.GetConfigPath(Config.Root) + ".tmp")).IsFalse();
    }

    [Test]
    public async Task Mutate_survives_a_transient_reader_holding_the_destination() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with { MachineId = "before" });

        // Share-read only (no FILE_SHARE_DELETE): on Windows this blocks the replace-into-place
        // until released, exercising Publish's retry; on Unix rename is unaffected.
        var reader = new FileStream(AppConfig.GetConfigPath(Config.Root), FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            var mutate = ConfigMutator.MutateAsync(Config.Root, c => c with { MachineId = "after" });
            await Task.Delay(100);
            reader.Dispose();
            await mutate;
        } finally {
            reader.Dispose();
        }

        var final = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(final.MachineId).IsEqualTo("after");
    }

    [Test]
    public async Task Legacy_v1_config_is_migrated_in_memory_and_persisted_through_the_mutation() {
        // minimal v1 flat config (no "version"/"profiles" — ConfigMigration's v1 shape)
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), """{"server_url":"https://legacy.example"}""");

        var result = await ConfigMutator.MutateAsync(Config.Root, c => c with { MachineId = "post-migration" });

        await Assert.That(result.Version).IsEqualTo(2);
        await Assert.That(result.MachineId).IsEqualTo("post-migration");
        // migration survived the same publication as the mutation
        var reread = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(reread.Version).IsEqualTo(2);
        await Assert.That(reread.MachineId).IsEqualTo("post-migration");
    }

    [Test]
    public async Task MachineIdProvider_heals_a_blank_machine_id_left_by_a_stale_or_broken_writer() {
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), """{"version":2,"machine_id":""}""");

        var id = await MachineIdProvider.GetOrCreateAsync(Config.Root);

        await Assert.That(id).Matches("^mach-[0-9a-f]{12}$");
        var reread = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(reread.MachineId).IsEqualTo(id);
    }

    // ── TryLoadPure: absence-vs-unreadable, at explicit paths (no shared-config-dir contention) ──

    [Test]
    public async Task TryLoadPure_absent_file_is_success_with_defaults() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        var ok = ConfigMutator.TryLoadPure(path, out var config);
        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task TryLoadPure_directory_in_place_of_file_is_failure_not_absence() {
        // File.Exists alone reads a directory as absent — TryLoadPure must not make that mistake:
        // a directory sitting at the config path is unreadable evidence, never "nothing configured".
        using var tmp = new TempDir();
        var path = tmp.CreateDir("config.json");

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
        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        await File.WriteAllTextAsync(path, "{not json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    [Test]
    public async Task TryLoadPure_non_object_root_is_failure_not_absence() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        await File.WriteAllTextAsync(path, "[1,2,3]");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task LoadPure_still_degrades_malformed_json_to_defaults() {
        // LoadPure discards TryLoadPure's bool — its own soft contract (always usable defaults) is
        // unchanged by the TryLoadPure hardening above.
        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        await File.WriteAllTextAsync(path, "{not json");

        var config = ConfigMutator.LoadPure(path);

        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    /// <summary>A non-not-found I/O failure, forced deterministically: the config path's PARENT
    /// is a plain file rather than a directory, so opening through it fails structurally.</summary>
    [Test]
    public async Task TryLoadPure_parent_replaced_by_a_file_is_failure_not_absence() {
        using var tmp = new TempDir();
        var parentAsFile = tmp.PathTo("not-a-directory");
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
        using var tmp = new TempDir();
        var grandparentAsFile = tmp.PathTo("not-a-directory");
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

        using var tmp = new TempDir();
        var link = tmp.PathTo("danglink");
        Directory.CreateSymbolicLink(link, tmp.PathTo("never-created-target"));
        var path = Path.Combine(link, "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>A dangling symlink AT the exact config path (not an ancestor) raises
    /// <see cref="FileNotFoundException"/>, not <see cref="DirectoryNotFoundException"/> — must still
    /// classify as unreadable, never the takeover-safe "nothing configured yet".</summary>
    [Test]
    public async Task TryLoadPure_dangling_symlink_at_exact_path_is_failure_not_absence() {
        Skip.When(OperatingSystem.IsWindows(), "symlink creation needs elevated privilege on Windows CI");

        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        File.CreateSymbolicLink(path, tmp.PathTo("never-created-target"));

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsFalse();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue(); // still a usable default out param
    }

    /// <summary>A genuinely never-created parent chain must still read as absence — disambiguating
    /// a blocked ancestor must not turn every missing directory level into a false failure.</summary>
    [Test]
    public async Task TryLoadPure_missing_parent_directory_is_still_absence() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("never-created", "config.json");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles.ContainsKey("default")).IsTrue();
    }

    [Test]
    public async Task TryLoadPure_valid_file_is_success() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("config.json");
        await File.WriteAllTextAsync(path, """{"version":2,"profiles":{"work":{"server_url":"https://w.example"}}}""");

        var ok = ConfigMutator.TryLoadPure(path, out var config);

        await Assert.That(ok).IsTrue();
        await Assert.That(config.Profiles["work"].ServerUrl).IsEqualTo("https://w.example");
    }

    [Test]
    public async Task LoadProfileConfig_is_pure_and_never_writes() {
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), """{"server_url":"https://legacy.example"}""");

        var cfg = await AppConfig.LoadProfileConfig(Config.Root);

        await Assert.That(cfg.Version).IsEqualTo(2);           // migrated in memory
        // NOTE: LoadProfileConfig routes persistence through MutateAsync — one write is
        // allowed here (the legacy-persist behavior), but the FILE must now be v2 and valid.
        var reread = JsonDocument.Parse(await File.ReadAllTextAsync(AppConfig.GetConfigPath(Config.Root)));
        await Assert.That(reread.RootElement.TryGetProperty("version", out var v) && v.GetInt32() == 2).IsTrue();
    }
}
