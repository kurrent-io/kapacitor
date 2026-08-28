namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Filesystem layout for SST OpenCode. OpenCode keeps config under
/// <c>~/.config/opencode</c> and auto-loads
/// plugins from <c>~/.config/opencode/plugins/</c> (honoring
/// <c>OPENCODE_CONFIG_DIR</c>, then <c>XDG_CONFIG_HOME</c>); session data lives
/// under <c>~/.local/share/opencode</c> (honoring <c>XDG_DATA_HOME</c>).
///
/// OpenCode has <b>no shell hooks</b>, so kcap's live integration ships as a
/// plugin file (<see cref="KcapPlugin"/>, written by <c>kcap plugin --opencode</c>)
/// rather than a hooks.json, mirroring the Pi extension model (<see cref="Pi.PiPaths"/>).
/// </summary>
public sealed class OpenCodePaths {
    /// <param name="configDir">OPENCODE_CONFIG_DIR, which replaces the config root outright.</param>
    /// <param name="xdgConfigHome">XDG_CONFIG_HOME, the parent of the <c>opencode</c> config leaf.</param>
    /// <param name="xdgDataHome">XDG_DATA_HOME, the parent of the <c>opencode</c> data leaf.</param>
    public OpenCodePaths(UserHome home, string? configDir, string? xdgConfigHome, string? xdgDataHome) {
        ConfigDir = !string.IsNullOrWhiteSpace(configDir) ? configDir
                  : !string.IsNullOrEmpty(xdgConfigHome) ? Path.Combine(xdgConfigHome, "opencode")
                  :                                        Path.Combine(home.Path, ".config", "opencode");

        DataDir = !string.IsNullOrEmpty(xdgDataHome)
            ? Path.Combine(xdgDataHome, "opencode")
            : Path.Combine(home.Path, ".local", "share", "opencode");

        // kcap's own record of what it has imported, so an OpenCode override cannot move it.
        ImportLedgerJson = Path.Combine(home.Path, ".cache", "kcap", "opencode-imported.json");
    }

    /// <summary>Reads the three overrides OpenCode honours; the home comes from the caller.</summary>
    public static OpenCodePaths FromEnvironment(UserHome home) => new(
        home,
        Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR"),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

    public string ConfigDir { get; }
    public string DataDir   { get; }

    /// <summary>Which OpenCode sessions kcap has already imported.</summary>
    public string ImportLedgerJson { get; }

    /// <summary>Auto-discovered global plugins dir; kcap installs <see cref="KcapPlugin"/> here.</summary>
    public string PluginsDir => Path.Combine(ConfigDir, "plugins");

    public string KcapPlugin => Path.Combine(PluginsDir, "kcap.ts");

    /// <summary>Marker recording the installed plugin version (sibling of kcap.ts).</summary>
    public string KcapPluginMarker => Path.Combine(PluginsDir, ".kcap-extension-version");

    /// <summary>
    /// OpenCode's config file (<c>~/.config/opencode/opencode.json</c>), where kcap registers its
    /// MCP servers under the <c>mcp</c> block (see <see cref="Mcp.McpConfigShape.OpenCode"/>).
    /// </summary>
    public string McpConfigJson => Path.Combine(ConfigDir, "opencode.json");

    /// <summary>
    /// OpenCode's user-global agent-instructions file (<c>~/.config/opencode/AGENTS.md</c>), where
    /// kcap installs its marker-delimited steering block.
    /// </summary>
    public string AgentsMd => Path.Combine(ConfigDir, "AGENTS.md");

    /// <summary>
    /// Detection by OpenCode's config or data dir presence — OpenCode creates one
    /// on first run. The binary name <c>opencode</c> can also be probed by callers
    /// via <c>BinaryProbe.OnPath("opencode")</c>.
    /// </summary>
    public bool IsInstalled => Directory.Exists(ConfigDir) || Directory.Exists(DataDir);
}
