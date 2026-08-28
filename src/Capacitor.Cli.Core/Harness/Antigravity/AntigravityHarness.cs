using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Harness.Antigravity;

/// <summary>
/// Antigravity as this process sees it: one vendor over two product roots, both under Gemini's.
/// There is no <c>FromEnvironment</c> on purpose — the layout is composed from a Gemini instance,
/// so the shared root is read once and cannot be derived twice into disagreeing values.
/// </summary>
public sealed class AntigravityHarness : IHarness<AntigravityHarness> {
    AntigravityHarness(AntigravityPaths paths) => Paths = paths;

    public static AntigravityHarness Over(GeminiHarness gemini) => new(new AntigravityPaths(gemini.Paths));

    public static HarnessId Id    => HarnessId.Antigravity;
    public static string    Label => "Antigravity";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public AntigravityPaths Paths { get; }

    // The CLI binary is `agy`, not `antigravity`, so both names must be probed or an agy-only
    // machine reads as absent. The marker covers either product root.
    public HarnessSignals Signals => new() {
        Binaries  = ["antigravity", "agy"],
        Installed = () => Paths.IsInstalled,
        Wired     = () => AntigravityHooksInstaller.IsInstalled(Paths.GlobalHooksJson),
    };
}
