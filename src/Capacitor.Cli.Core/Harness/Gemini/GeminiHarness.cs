namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>Gemini as this process sees it.</summary>
public sealed class GeminiHarness : IHarness<GeminiHarness> {
    GeminiHarness(GeminiPaths paths) => Paths = paths;

    /// <summary>Resolves Gemini's one override, <c>GEMINI_CLI_HOME</c> — which names the PARENT of
    /// <c>.gemini</c>, not the directory itself. <c>GEMINI_HOME</c> is not a Gemini variable and is
    /// deliberately not honoured.</summary>
    public static GeminiHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("GEMINI_CLI_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static GeminiHarness Over(GeminiPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Gemini;
    public static string    Label => "Gemini";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public GeminiPaths Paths { get; }

    // The marker covers IDE-launched Gemini; the PATH probe covers a fresh install that has not
    // created ~/.gemini yet.
    public HarnessSignals Signals => new() {
        Binaries  = ["gemini"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => GeminiHooksInstaller.IsInstalled(Paths.SettingsJson),
    };
}
