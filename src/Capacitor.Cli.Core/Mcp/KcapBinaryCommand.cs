namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// The command value written into generated harness MCP configs: the absolute path of the
/// running native binary, so the harness spawns it directly instead of resolving `kcap` to the
/// npm node wrapper (one resident Node runtime per MCP server per session otherwise). When
/// `kcap setup` runs via the wrapper the process is ALREADY the native binary — the wrapper
/// exec's it — so <see cref="Environment.ProcessPath"/> IS the platform binary path.
/// </summary>
public static class KcapBinaryCommand {
    /// <summary>Resolves the command to register. <paramref name="resolveBinaryPath"/> is the
    /// test seam (null = the real <see cref="Environment.ProcessPath"/>); a null/blank resolution
    /// falls back to the PATH-resolved literal <c>"kcap"</c>, which keeps working via the wrapper.</summary>
    public static string Resolve(Func<string?>? resolveBinaryPath = null) {
        string? path = null;
        try { path = resolveBinaryPath is null ? Environment.ProcessPath : resolveBinaryPath(); }
        catch { /* fall back to the PATH-resolved wrapper command */ }
        return string.IsNullOrWhiteSpace(path) ? KcapMcpServers.Command : path;
    }
}
