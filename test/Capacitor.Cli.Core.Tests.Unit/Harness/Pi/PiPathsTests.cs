using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

public class PiPathsTests {
    static PiPaths Under(string home, string? agentDir = null) => new(new(home), agentDir);

    [Test]
    public async Task Agent_dir_override_is_used_verbatim_as_the_agent_leaf() {
        await Assert.That(Under("/fake/home", "/custom/agent").AgentDir).IsEqualTo("/custom/agent");
    }

    [Test]
    public async Task Agent_dir_override_expands_a_leading_tilde_against_home() {
        // Contract: a leading "~/" is replaced by home; the remainder ("pi/agent")
        // is appended verbatim as one segment. Expected must Path.Combine the
        // remainder as a single segment too — combining it as ("pi", "agent")
        // would rewrite its inner separator to "\" on Windows and mismatch.
        await Assert.That(Under("/fake/home", "~/pi/agent").AgentDir)
            .IsEqualTo(Path.Combine("/fake/home", "pi/agent"));
    }

    [Test]
    public async Task Members_default_under_dot_pi_agent_in_the_home() {
        var paths    = Under("/fake/home");
        var agentDir = Path.Combine("/fake/home", ".pi", "agent");

        await Assert.That(paths.AgentDir).IsEqualTo(agentDir);
        await Assert.That(paths.SessionsDir).IsEqualTo(Path.Combine(agentDir, "sessions"));
        // kcap-mcp.ts lives in extensions/ (beside kcap.ts); AGENTS.md is directly under agent/.
        await Assert.That(paths.KcapExtension).IsEqualTo(Path.Combine(agentDir, "extensions", "kcap.ts"));
        await Assert.That(paths.KcapMcpExtension)
            .IsEqualTo(Path.Combine(agentDir, "extensions", "kcap-mcp.ts"));
        await Assert.That(paths.KcapMcpExtensionMarker)
            .IsEqualTo(Path.Combine(agentDir, "extensions", ".kcap-mcp-extension-version"));
        await Assert.That(paths.AgentsMd).IsEqualTo(Path.Combine(agentDir, "AGENTS.md"));
    }

    [Test]
    public async Task IsInstalled_follows_the_agent_dir() {
        await Assert.That(Under("/nonexistent", Tmp.Path).IsInstalled).IsTrue();
        await Assert.That(Under("/nonexistent", "/also-nonexistent").IsInstalled).IsFalse();
    }

    // Bare: PI_CODING_AGENT_DIR is inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_takes_PI_CODING_AGENT_DIR_as_the_agent_leaf() {
        // The env value IS the leaf — no extra /agent appended, unlike the home default.
        var relocated = Path.Combine(Path.GetTempPath(), "kcap-pi-agent");

        using var env = EnvScope.Exclusive("PI_CODING_AGENT_DIR", relocated);

        await Assert.That(PiHarness.FromEnvironment(new("/fake/home")).Paths.AgentDir).IsEqualTo(relocated);
    }

    [Test]
    [NotInParallel]
    public async Task FromEnvironment_without_the_override_falls_back_to_the_home() {
        using var env = EnvScope.Exclusive("PI_CODING_AGENT_DIR", null);

        await Assert.That(PiHarness.FromEnvironment(new("/fake/home")).Paths.AgentDir)
            .IsEqualTo(Path.Combine("/fake/home", ".pi", "agent"));
    }

    [TempDir] public required TempDir Tmp { get; init; }
}
