namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// How one harness projects the kcap MCP servers into its own config: which subset it receives, which
/// on-disk shape renders it, and the ownership-marker name it is written under.
///
/// <para><b>Why this exists.</b> That tuple used to be written out twice for every harness — once in
/// <c>PluginCommand</c>'s per-target registration and once in <c>SetupCommand</c>'s installer
/// delegates — with nothing tying the two together. Either copy could drop a server, pick the wrong
/// shape, or use a different marker, and the other route would keep working, so a harness could
/// silently project a different set of tools depending on whether the user ran
/// <c>kcap plugin install</c> or <c>kcap setup</c>. Conformance tests can only catch that by
/// exercising both routes; one definition makes it unrepresentable.</para>
/// </summary>
public sealed record HarnessMcpProjection(
    string                        Harness,
    IReadOnlyList<KcapMcpServer>  Servers,
    McpConfigShape                Shape
) {
    /// <summary>Writes this harness's kcap servers into <paramref name="configPath"/>. The marker name
    /// is derived from the harness rather than passed in, so the two call sites cannot disagree about
    /// which entries kcap owns — a mismatch there would strand entries on uninstall.</summary>
    public JsonMcpConfigWriter.Change Register(string configPath, string? cwd = null,
                                               Func<string?>? resolveBinaryPath = null) =>
        JsonMcpConfigWriter.Register(configPath, Servers, Shape, cwd, new McpMarker(Harness), resolveBinaryPath);

    public JsonMcpConfigWriter.Change Unregister(string configPath) =>
        JsonMcpConfigWriter.Unregister(configPath, Shape, new McpMarker(Harness));

    /// <summary>Whether kcap currently owns any entry in this harness's config — the "is the MCP
    /// half already installed?" probe. Here rather than at the call site for the same reason as the
    /// marker itself: a probe reading a DIFFERENT ownership tuple than the writer would report an
    /// existing install as absent, and the refresh path would then skip it.</summary>
    public bool OwnsAnything(string configPath) => new McpMarker(Harness).Owned(configPath).Any();
}

/// <summary>The JSON-config harnesses, and the single definition of what each one gets.
///
/// <para>Codex is absent on purpose — its registration is TOML with its own ownership ledger
/// (<c>CodexConfigToml</c>), not this writer. Claude Code is absent because it reads a bundled static
/// <c>kcap/.mcp.json</c> rather than anything generated. Pi is absent because it registers no MCP
/// config at all — it emits a bridge that discovers tools at runtime.</para></summary>
public static class HarnessMcpProjections {
    // Every non-Claude JSON harness receives the same subset: kcap-workitems is Claude-Code-plugin
    // only (its session-id default rides the Claude hook env).
    public static readonly HarnessMcpProjection Cursor      = new("cursor",      KcapMcpServers.ForCursor, McpConfigShape.Standard);
    public static readonly HarnessMcpProjection Copilot     = new("copilot",     KcapMcpServers.ForCursor, McpConfigShape.Copilot);
    public static readonly HarnessMcpProjection Gemini      = new("gemini",      KcapMcpServers.ForCursor, McpConfigShape.Gemini);
    public static readonly HarnessMcpProjection Kiro        = new("kiro",        KcapMcpServers.ForCursor, McpConfigShape.Standard);
    public static readonly HarnessMcpProjection OpenCode    = new("opencode",    KcapMcpServers.ForCursor, McpConfigShape.OpenCode);
    public static readonly HarnessMcpProjection Antigravity = new("antigravity", KcapMcpServers.ForCursor, McpConfigShape.Standard);

    public static readonly IReadOnlyList<HarnessMcpProjection> All =
        [Cursor, Copilot, Gemini, Kiro, OpenCode, Antigravity];
}
