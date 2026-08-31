namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>Claude Code as this process sees it.</summary>
public sealed class ClaudeHarness : IHarness<ClaudeHarness> {
    ClaudeHarness(ClaudePaths paths) => Paths = paths;

    /// <summary>Resolves Claude's one override, <c>CLAUDE_CONFIG_DIR</c>, which replaces
    /// <c>~/.claude</c> wholesale.</summary>
    public static ClaudeHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static ClaudeHarness Over(ClaudePaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Claude;
    public static string    Label => "Claude Code";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public ClaudePaths Paths { get; }

    // PATH-only: ~/.claude exists on machines that never ran Claude — kcap's own skills
    // install creates it — so a marker here would report a vendor that is not there.
    public HarnessSignals Signals => new() {
        Binaries  = ["claude"],
        Wired     = () => ClaudePluginInstaller.IsPluginEnabled(Paths.UserSettings),
    };
}
