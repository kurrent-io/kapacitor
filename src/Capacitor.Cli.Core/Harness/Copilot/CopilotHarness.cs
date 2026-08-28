namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>Copilot as this process sees it.</summary>
public sealed class CopilotHarness : IHarness<CopilotHarness> {
    CopilotHarness(CopilotPaths paths) => Paths = paths;

    /// <summary>Resolves Copilot's one override, <c>COPILOT_HOME</c>, which replaces the
    /// whole <c>~/.copilot</c> path.</summary>
    public static CopilotHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("COPILOT_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static CopilotHarness Over(CopilotPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Copilot;
    public static string    Label => "Copilot";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public CopilotPaths Paths { get; }

    // The marker covers users who launch Copilot through an IDE wrapper; the PATH probe covers
    // a fresh install that has not created ~/.copilot yet.
    public HarnessSignals Signals => new() {
        Binaries  = ["copilot"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => CopilotHooksInstaller.IsInstalled(Paths.KcapHooksJson),
    };
}
