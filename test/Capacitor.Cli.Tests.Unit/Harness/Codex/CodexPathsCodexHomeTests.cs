using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexPathsCodexHomeTests {
    [Test]
    public async Task Home_codex_home_param_wins_over_home() {
        await Assert.That(CodexPaths.Home(home: "/fake/home", codexHome: "/relocated/codex"))
            .IsEqualTo("/relocated/codex");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Home_and_derived_members_resolve_default_then_env_override() {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            await Assert.That(CodexPaths.Home(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".codex"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-cfg");
            Environment.SetEnvironmentVariable("CODEX_HOME", relocated);
            await Assert.That(CodexPaths.Home()).IsEqualTo(relocated);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(relocated, "sessions"));
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(relocated, "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task PluginEnvironment_CodexHome_delegates_and_honors_override() {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        var env = new PluginEnvironment("/fake/home", new ProfileConfig(), () => null, TextWriter.Null, TextWriter.Null);
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            await Assert.That(env.CodexHome).IsEqualTo(Path.Combine("/fake/home", ".codex"));
            await Assert.That(env.CodexConfigTomlPath)
                .IsEqualTo(Path.Combine("/fake/home", ".codex", "config.toml"));

            var relocated = Path.Combine(Path.GetTempPath(), "kcap-codex-pe");
            Environment.SetEnvironmentVariable("CODEX_HOME", relocated);
            await Assert.That(env.CodexHome).IsEqualTo(relocated);
            await Assert.That(env.CodexConfigTomlPath).IsEqualTo(Path.Combine(relocated, "config.toml"));
        } finally {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }
}
