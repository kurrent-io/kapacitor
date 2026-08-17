using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

public class PiPathsTests {
    // Parallel-safe: override param is non-null, so no env var is read.
    [Test]
    public async Task AgentDir_param_is_used_verbatim_as_the_agent_leaf() {
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: "/custom/agent"))
            .IsEqualTo("/custom/agent");
    }

    [Test]
    public async Task AgentDir_expands_leading_tilde_against_home() {
        // Contract: a leading "~/" is replaced by home; the remainder ("pi/agent")
        // is appended verbatim as one segment. Expected must Path.Combine the
        // remainder as a single segment too — combining it as ("pi", "agent")
        // would rewrite its inner separator to "\" on Windows and mismatch.
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: "~/pi/agent"))
            .IsEqualTo(Path.Combine("/fake/home", "pi/agent"));
    }

    [Test]
    public async Task AgentDir_defaults_to_dot_pi_agent_under_home() {
        await Assert.That(PiPaths.AgentDir(home: "/fake/home", agentDir: null))
            .IsEqualTo(Path.Combine("/fake/home", ".pi", "agent"));
    }

    // Parallel-safe: home is non-null, so no env var is read.
    [Test]
    public async Task McpExtension_and_AgentsMd_sit_under_the_agent_dir() {
        var agentDir = Path.Combine("/fake/home", ".pi", "agent");
        // kcap-mcp.ts lives in extensions/ (beside kcap.ts); AGENTS.md is directly under agent/.
        await Assert.That(PiPaths.KcapMcpExtension(home: "/fake/home"))
            .IsEqualTo(Path.Combine(agentDir, "extensions", "kcap-mcp.ts"));
        await Assert.That(PiPaths.KcapMcpExtensionMarker(home: "/fake/home"))
            .IsEqualTo(Path.Combine(agentDir, "extensions", ".kcap-mcp-extension-version"));
        await Assert.That(PiPaths.AgentsMd(home: "/fake/home"))
            .IsEqualTo(Path.Combine(agentDir, "AGENTS.md"));
    }

    // Parallel-safe: both params are non-null, so no env var is read — mirrors
    // KiroPaths.IsInstalled(home, kiroHome) / OpenCodePaths.IsInstalled(...).
    [Test]
    public async Task IsInstalled_honors_injected_agent_dir_override_without_env() {
        using var tmp = new TempDir();
        await Assert.That(PiPaths.IsInstalled(home: "/nonexistent", agentDir: tmp.Path)).IsTrue();
        await Assert.That(PiPaths.IsInstalled(home: "/nonexistent", agentDir: "/also-nonexistent")).IsFalse();
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task AgentDir_reads_PI_CODING_AGENT_DIR_and_derived_members_follow() {
        var original = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        try {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            await Assert.That(PiPaths.AgentDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".pi", "agent"));

            // Env value is the agent leaf (NO extra /agent appended); extensions follow.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-pi-agent");
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", relocated);
            await Assert.That(PiPaths.AgentDir()).IsEqualTo(relocated);
            await Assert.That(PiPaths.KcapExtension())
                .IsEqualTo(Path.Combine(relocated, "extensions", "kcap.ts"));
            await Assert.That(PiPaths.KcapMcpExtension())
                .IsEqualTo(Path.Combine(relocated, "extensions", "kcap-mcp.ts"));
            await Assert.That(PiPaths.AgentsMd())
                .IsEqualTo(Path.Combine(relocated, "AGENTS.md"));
            await Assert.That(PiPaths.SessionsDir())
                .IsEqualTo(Path.Combine(relocated, "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", original);
        }
    }
}
