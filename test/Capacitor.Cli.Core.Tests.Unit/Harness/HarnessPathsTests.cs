using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// Each vendor's paths come from its own factory rather than this bundle reading the override
/// variables itself, so moving one vendor's override doesn't move the others. Antigravity is
/// composed from the same Gemini instance, since two independent derivations of one variable
/// could disagree.
/// </summary>
public class HarnessPathsTests {
    // Bare: the overrides are inherited by any child a concurrent test spawns.
    [Test, NotInParallel]
    public async Task Each_vendor_is_routed_through_its_own_override() {
        using var relocated = new TempDir();

        using var claude = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", relocated.PathTo("claude"));
        using var codex  = EnvScope.Exclusive("CODEX_HOME", relocated.PathTo("codex"));
        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);
        using var kiro   = EnvScope.Exclusive("KIRO_HOME", relocated.PathTo("kiro"));

        var paths = HarnessPaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Claude.Home).IsEqualTo(relocated.PathTo("claude"));
        await Assert.That(paths.Codex.Home).IsEqualTo(relocated.PathTo("codex"));
        await Assert.That(paths.Gemini.Root).IsEqualTo(relocated.PathTo(".gemini"));
        await Assert.That(paths.Kiro.ConfigRoot).IsEqualTo(relocated.PathTo("kiro"));
        // Untouched variables leave their vendor on the injected home.
        await Assert.That(paths.Agents.Home).IsEqualTo(Path.Combine("/fake/home", ".agents"));
    }

    [Test, NotInParallel]
    public async Task Antigravity_hangs_off_the_same_gemini_root() {
        using var relocated = new TempDir();

        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);

        var paths = HarnessPaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Antigravity.McpConfigJson).StartsWith(paths.Gemini.Root);
    }
}
