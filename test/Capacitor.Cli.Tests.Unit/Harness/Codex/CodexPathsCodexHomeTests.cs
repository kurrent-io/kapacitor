using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexPathsCodexHomeTests {
    [Test]
    public async Task Codex_home_override_replaces_the_whole_root() {
        var paths = new CodexPaths(new("/fake/home"), "/relocated.Path/codex");

        await Assert.That(paths.Home).IsEqualTo("/relocated.Path/codex");
        await Assert.That(paths.Sessions).IsEqualTo(Path.Combine("/relocated.Path/codex", "sessions"));
        await Assert.That(paths.UserHooksJson).IsEqualTo(Path.Combine("/relocated.Path/codex", "hooks.json"));
    }

    // Bare: CODEX_HOME is inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_CODEX_HOME() {
        using var relocated = new TempDir();

        using var env = EnvScope.Exclusive("CODEX_HOME", relocated.Path);

        await Assert.That(CodexHarness.FromEnvironment(new("/fake/home")).Paths.Home).IsEqualTo(relocated.Path);
    }

    [Test]
    [NotInParallel]
    public async Task FromEnvironment_without_the_override_falls_back_to_the_home() {
        using var env = EnvScope.Exclusive("CODEX_HOME", null);

        await Assert.That(CodexHarness.FromEnvironment(new("/fake/home")).Paths.Home)
            .IsEqualTo(Path.Combine("/fake/home", ".codex"));
    }
}
