namespace Capacitor.Cli.Core.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class AgentsPathsTests {
    [Test]
    public async Task Home_resolves_under_HOME_dot_agents() {
        using var tmp = new TempDir();
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.Home; // force any static init
            Environment.SetEnvironmentVariable("HOME", tmp.Path);
            await Assert.That(AgentsPaths.Home).IsEqualTo(tmp.PathTo(".agents"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Test]
    public async Task UserSkillsDir_resolves_under_HOME_dot_agents_skills() {
        using var tmp = new TempDir();
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = AgentsPaths.UserSkillsDir;
            Environment.SetEnvironmentVariable("HOME", tmp.Path);
            await Assert.That(AgentsPaths.UserSkillsDir)
                .IsEqualTo(tmp.PathTo(".agents", "skills"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }
}
