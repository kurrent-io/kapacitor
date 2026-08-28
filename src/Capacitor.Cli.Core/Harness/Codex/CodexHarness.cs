namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Codex as this process sees it.</summary>
public sealed class CodexHarness : IHarness<CodexHarness> {
    CodexHarness(CodexPaths paths) => Paths = paths;

    /// <summary>Resolves Codex's one override, <c>CODEX_HOME</c>.</summary>
    public static CodexHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("CODEX_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static CodexHarness Over(CodexPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Codex;
    public static string    Label => "Codex";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public CodexPaths Paths { get; }

    // PATH-only, for the same reason as Claude: ~/.codex is created by things other than a
    // Codex run.
    public HarnessSignals Signals => new() {
        Binaries  = ["codex"],
        Wired     = () => CodexHooksInstaller.ReferencesKcapHook(Paths.UserHooksJson),
    };
}
