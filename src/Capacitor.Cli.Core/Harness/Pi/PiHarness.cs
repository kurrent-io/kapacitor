namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>Pi as this process sees it.</summary>
public sealed class PiHarness : IHarness<PiHarness> {
    PiHarness(PiPaths paths) => Paths = paths;

    /// <summary>Resolves Pi's one override, <c>PI_CODING_AGENT_DIR</c>.</summary>
    public static PiHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static PiHarness Over(PiPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Pi;
    public static string    Label => "Pi";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public PiPaths Paths { get; }

    // Pi keeps state under ~/.pi/agent; the PATH probe covers an install that has not created it.
    public HarnessSignals Signals => new() {
        Binaries  = ["pi"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => PiExtensionInstaller.IsInstalled(Paths.KcapExtension),
    };
}
