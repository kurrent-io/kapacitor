namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="ConfigRoot"/> — how a filename becomes a path under the root, and how the
/// root itself is resolved at a process entry point.
/// </summary>
public class ConfigRootTests {
    [Test]
    public async Task Path_joins_segments_under_the_directory() {
        var root = new ConfigRoot(Path.Combine("opt", "kcap"));

        await Assert.That(root.Path("config.json")).IsEqualTo(Path.Combine("opt", "kcap", "config.json"));
        await Assert.That(root.Path("cache", "auth-providers.json"))
                    .IsEqualTo(Path.Combine("opt", "kcap", "cache", "auth-providers.json"));
        await Assert.That(root.Path()).IsEqualTo(root.Directory);
    }

    /// <summary>The reason the body uses <c>Path.Join</c>: <c>Path.Combine</c> would drop the root
    /// and return the rooted segment alone. Not containment — <c>Join</c> normalises nothing, so a
    /// <c>..</c> segment still escapes; no caller passes a segment it did not write itself.</summary>
    [Test]
    public async Task Path_keeps_the_root_when_a_segment_looks_rooted() {
        var root = new ConfigRoot(Path.Combine(Path.GetTempPath(), "kcap-root"));

        var joined = root.Path($"{Path.DirectorySeparatorChar}etc", "passwd");

        await Assert.That(joined).StartsWith(root.Directory);
    }

    [Test, NotInParallel]
    public async Task Default_directory_lives_under_the_config_folder() {
        using var env = EnvScope.Exclusive(ConfigRoot.ConfigDirEnvVar, null);

        await Assert.That(ConfigRoot.FromEnvironment().Directory)
                    .IsEqualTo(Path.Combine(PathHelpers.HomeDirectory, ".config", "kcap"));
    }

    [Test, NotInParallel]
    public async Task Environment_value_wins_over_the_home_fallback() {
        var fallback = Path.Combine(PathHelpers.HomeDirectory, ".config", "kcap");

        using (var env = EnvScope.Exclusive(ConfigRoot.ConfigDirEnvVar, "/elsewhere"))
            await Assert.That(ConfigRoot.FromEnvironment().Directory).IsEqualTo("/elsewhere");

        // Empty reads as unset, or `export KCAP_CONFIG_DIR=` puts every config file in the cwd —
        // which is what the old `?? Path.Combine(...)` static did.
        using (var env = EnvScope.Exclusive(ConfigRoot.ConfigDirEnvVar, ""))
            await Assert.That(ConfigRoot.FromEnvironment().Directory).IsEqualTo(fallback);
    }
}
