using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins <see cref="McpMarker"/>'s central marker root to a throwaway temp dir for the whole test
/// assembly (mirrors <see cref="DaemonPathsGlobalSetup"/>). Without it a real <c>McpMarker</c>
/// resolves the central store under the real user profile (<c>~/.kcap/mcp-markers</c>), which
/// parallel suites raced on and which pollutes the developer's home. Set once before any test runs —
/// no per-test override, and no window where a test falls back to the real dir.
/// </summary>
public class McpMarkerGlobalSetup {
    internal static readonly string SharedMarkerRoot = Path.Combine(
        Path.GetTempPath(),
        "kcap-mcp-markers-tests-" + Guid.NewGuid().ToString("N")[..8]
    );

    [Before(Assembly)]
    public static void PinCentralMarkerRoot() {
        Directory.CreateDirectory(SharedMarkerRoot);
        McpMarker.OverrideCentralRootForTesting(SharedMarkerRoot);
    }

    // Delete the temp root but leave the override pinned: a late/background McpMarker call after
    // teardown then just recreates a file under the (removed) temp root, never the real ~/.kcap —
    // nulling the override here would reopen that real-home fallback window.
    [After(Assembly)]
    public static void CleanupCentralMarkerRoot() {
        try { Directory.Delete(SharedMarkerRoot, recursive: true); } catch { /* best effort */ }
    }
}
