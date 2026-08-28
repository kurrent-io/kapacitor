namespace Capacitor.Cli.Core.Tests.Unit;

public class AgentsPathsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Paths_resolve_under_the_injected_home() {
        var paths = new AgentsPaths(new(Tmp.Path));

        await Assert.That(paths.Home).IsEqualTo(Tmp.PathTo(".agents"));
        await Assert.That(paths.UserSkillsDir).IsEqualTo(Tmp.PathTo(".agents", "skills"));
    }
}
