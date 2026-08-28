namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>OpenCode as this process sees it.</summary>
public sealed class OpenCodeHarness : IHarness<OpenCodeHarness> {
    OpenCodeHarness(OpenCodePaths paths) => Paths = paths;

    /// <summary>Resolves the three overrides OpenCode honours: <c>OPENCODE_CONFIG_DIR</c> replaces
    /// the config root outright, then <c>XDG_CONFIG_HOME</c> and <c>XDG_DATA_HOME</c> parent its
    /// leaves.</summary>
    public static OpenCodeHarness FromEnvironment(UserHome home) => Over(new(
        home,
        Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR"),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static OpenCodeHarness Over(OpenCodePaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.OpenCode;
    public static string    Label => "OpenCode";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public OpenCodePaths Paths { get; }

    // Config and data dirs are the marker; the PATH probe covers a fresh install.
    public HarnessSignals Signals => new() {
        Binaries  = ["opencode"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => OpenCodeExtensionInstaller.IsInstalled(Paths.KcapPlugin),
    };
}
