using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// The bundle's composition: it calls each vendor's own factory rather than reading the override
/// variables itself, so a relocated vendor moves and the others do not. Antigravity is composed from
/// the SAME Gemini instance — two derivations from one variable could disagree.
/// </summary>
public class HarnessPathsTests {
    // Bare: the overrides are inherited by any child a concurrent test spawns.
    [Test, NotInParallel]
    public async Task Each_vendor_is_routed_through_its_own_override() {
        var relocated = Path.Combine(Path.GetTempPath(), "kcap-bundle");

        using var claude = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", Path.Combine(relocated, "claude"));
        using var codex  = EnvScope.Exclusive("CODEX_HOME", Path.Combine(relocated, "codex"));
        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated);
        using var kiro   = EnvScope.Exclusive("KIRO_HOME", Path.Combine(relocated, "kiro"));

        var paths = HarnessPaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Claude.Home).IsEqualTo(Path.Combine(relocated, "claude"));
        await Assert.That(paths.Codex.Home).IsEqualTo(Path.Combine(relocated, "codex"));
        await Assert.That(paths.Gemini.Root).IsEqualTo(Path.Combine(relocated, ".gemini"));
        await Assert.That(paths.Kiro.ConfigRoot).IsEqualTo(Path.Combine(relocated, "kiro"));
        // Untouched variables leave their vendor on the injected home.
        await Assert.That(paths.Agents.Home).IsEqualTo(Path.Combine("/fake/home", ".agents"));
    }

    [Test, NotInParallel]
    public async Task Antigravity_hangs_off_the_same_gemini_root() {
        var relocated = Path.Combine(Path.GetTempPath(), "kcap-bundle-agy");

        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated);

        var paths = HarnessPaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Antigravity.McpConfigJson).StartsWith(paths.Gemini.Root);
    }
}
