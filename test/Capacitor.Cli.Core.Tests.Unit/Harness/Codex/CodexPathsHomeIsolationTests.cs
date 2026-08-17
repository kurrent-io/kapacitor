using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

[NotInParallel("HomeEnvVarMutation")]
public class CodexPathsHomeIsolationTests {
    [Test]
    public async Task Home_reflects_current_HOME_env_var() {
        using var tmp = new TempDir();
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            // Force any prior static init that might cache HOME's value
            _ = CodexPaths.Home();

            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.Path);
            await Assert.That(CodexPaths.Home()).IsEqualTo(Path.Combine(tmp.Path, ".codex"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
    }

    [Test]
    public async Task Sessions_reflects_current_HOME_env_var() {
        using var tmp = new TempDir();
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            _ = CodexPaths.Sessions;
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.Path);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(tmp.Path, ".codex", "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
    }

    [Test]
    public async Task UserHooksJson_reflects_current_HOME_env_var() {
        using var tmp = new TempDir();
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            _ = CodexPaths.UserHooksJson;
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.Path);
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(tmp.Path, ".codex", "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
    }
}
