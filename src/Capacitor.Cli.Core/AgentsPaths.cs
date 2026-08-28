namespace Capacitor.Cli.Core;

/// <summary>The cross-vendor <c>~/.agents</c> tree — skills any harness can load.</summary>
public sealed class AgentsPaths(UserHome home) {
    public string Home          => Path.Combine(home.Path, ".agents");
    public string UserSkillsDir => Path.Combine(Home, "skills");
}
