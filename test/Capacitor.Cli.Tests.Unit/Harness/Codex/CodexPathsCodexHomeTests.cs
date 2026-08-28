using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexPathsCodexHomeTests {
    [Test]
    public async Task Codex_home_override_replaces_the_whole_root() {
        var paths = new CodexPaths(new("/fake/home"), "/relocated/codex");

        await Assert.That(paths.Home).IsEqualTo("/relocated/codex");
        await Assert.That(paths.Sessions).IsEqualTo(Path.Combine("/relocated/codex", "sessions"));
        await Assert.That(paths.UserHooksJson).IsEqualTo(Path.Combine("/relocated/codex", "hooks.json"));
    }

    // Bare: CODEX_HOME is inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_CODEX_HOME() {
        var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-cfg");

        using var env = EnvScope.Exclusive("CODEX_HOME", relocated);

        await Assert.That(CodexPaths.FromEnvironment(new("/fake/home")).Home).IsEqualTo(relocated);
    }

    [Test]
    [NotInParallel]
    public async Task FromEnvironment_without_the_override_falls_back_to_the_home() {
        using var env = EnvScope.Exclusive("CODEX_HOME", null);

        await Assert.That(CodexPaths.FromEnvironment(new("/fake/home")).Home)
            .IsEqualTo(Path.Combine("/fake/home", ".codex"));
    }

    /// <summary>The aggregate reads CODEX_HOME through <see cref="CodexPaths"/>, so a relocated
    /// Codex moves with it — resolved once per instance, which is why each half builds its own
    /// rather than re-reading one.</summary>
    [Test]
    [NotInParallel]
    public async Task Plugin_environment_codex_paths_honour_the_override() {
        static PluginEnvironment Env() =>
            new(new("/fake/home"), new ProfileConfig(), () => null, TextWriter.Null, TextWriter.Null);

        using (EnvScope.Exclusive("CODEX_HOME", null)) {
            var codex = Env().Paths.Codex;
            await Assert.That(codex.Home).IsEqualTo(Path.Combine("/fake/home", ".codex"));
            await Assert.That(codex.ConfigToml)
                .IsEqualTo(Path.Combine("/fake/home", ".codex", "config.toml"));
        }

        var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-pe");

        using (EnvScope.Exclusive("CODEX_HOME", relocated)) {
            var codex = Env().Paths.Codex;
            await Assert.That(codex.Home).IsEqualTo(relocated);
            await Assert.That(codex.ConfigToml).IsEqualTo(Path.Combine(relocated, "config.toml"));
        }
    }
}
