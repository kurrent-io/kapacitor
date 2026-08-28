namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>Kiro as this process sees it.</summary>
public sealed class KiroHarness : IHarness<KiroHarness> {
    KiroHarness(KiroPaths paths) => Paths = paths;

    /// <summary>Resolves Kiro's one override, <c>KIRO_HOME</c>.</summary>
    public static KiroHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("KIRO_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static KiroHarness Over(KiroPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Kiro;
    public static string    Label => "Kiro";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public KiroPaths Paths { get; }

    // Both names, because a machine can carry the CLI without the IDE.
    public HarnessSignals Signals => new() {
        Binaries  = ["kiro", "kiro-cli"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => KiroHooksInstaller.IsInstalled(Paths.KcapAgentJson),
    };
}
