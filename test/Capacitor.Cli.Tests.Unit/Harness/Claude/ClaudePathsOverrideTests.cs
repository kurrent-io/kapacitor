using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudePathsOverrideTests {
    // Parallel-safe: configDir is non-null, so the CLAUDE_CONFIG_DIR env var is never read.
    [Test]
    public async Task Home_config_dir_param_wins_over_home() {
        await Assert.That(ClaudePaths.Home(home: "/fake/home", configDir: "/relocated/claude"))
            .IsEqualTo("/relocated/claude");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Home_and_derived_members_resolve_default_then_env_override() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            // Default: no override -> ~/.claude under the injected home.
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(ClaudePaths.Home(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".claude"));

            // Override via env var -> verbatim, and derived members follow.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-cfg");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(ClaudePaths.Home()).IsEqualTo(relocated);
            await Assert.That(ClaudePaths.Projects).IsEqualTo(Path.Combine(relocated, "projects"));
            await Assert.That(ClaudePaths.UserSettings).IsEqualTo(Path.Combine(relocated, "settings.json"));
            await Assert.That(ClaudePaths.Plans).IsEqualTo(Path.Combine(relocated, "plans"));
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Test]
    public async Task UserConfigJson_config_dir_param_puts_json_inside_the_config_dir() {
        // Override param is non-null -> env var never read (parallel-safe).
        await Assert.That(ClaudePaths.UserConfigJson(home: "/fake/home", configDir: "/relocated"))
            .IsEqualTo(Path.Combine("/relocated", ".claude.json"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task UserConfigJson_default_is_sibling_of_claude_dir_and_env_relocates_inside() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            // Default: <home>/.claude.json (NOT <home>/.claude/.claude.json).
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(ClaudePaths.UserConfigJson(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".claude.json"));

            // Override via env var: .claude.json lives INSIDE the config dir.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-cfg");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(ClaudePaths.UserConfigJson())
                .IsEqualTo(Path.Combine(relocated, ".claude.json"));
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task PluginEnvironment_ClaudeHome_delegates_and_honors_override() {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var env = new PluginEnvironment("/fake/home", new ProfileConfig(), () => null, TextWriter.Null, TextWriter.Null);
        try {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            await Assert.That(env.ClaudeHome).IsEqualTo(Path.Combine("/fake/home", ".claude"));
            await Assert.That(env.ClaudeUserSettings)
                .IsEqualTo(Path.Combine("/fake/home", ".claude", "settings.json"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-pe");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);
            await Assert.That(env.ClaudeHome).IsEqualTo(relocated);
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }
}
