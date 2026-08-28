namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>Cursor as this process sees it.</summary>
public sealed class CursorHarness : IHarness<CursorHarness> {
    CursorHarness(CursorPaths paths) => Paths = paths;

    /// <summary>Cursor honours no override of its own, so the home and this host's own layout are
    /// all there is to resolve.</summary>
    public static CursorHarness FromEnvironment(UserHome home) => Over(new CursorPaths(home));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static CursorHarness Over(CursorPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Cursor;
    public static string    Label => "Cursor";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public CursorPaths Paths { get; }

    // No CLI to probe: Cursor ships as an editor, so its own state is the only signal.
    public HarnessSignals Signals => new() {
        Installed = () => Paths.IsInstalled,
        Wired     = () => CursorHooksInstaller.IsInstalled(Paths.UserHooksJson),
    };
}
