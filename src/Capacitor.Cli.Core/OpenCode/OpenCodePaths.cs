namespace Capacitor.Cli.Core.OpenCode;

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
public static class OpenCodePaths {
    public static string ConfigDir(string? home = null, string? configDir = null, string? xdgConfigHome = null) {
        configDir ??= Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return configDir;

        xdgConfigHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfigHome)) return Path.Combine(xdgConfigHome, "opencode");

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "opencode");
    }

    public static string DataDir(string? home = null, string? xdgDataHome = null) {
        xdgDataHome ??= Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome)) return Path.Combine(xdgDataHome, "opencode");

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "opencode");
    }

    /// <summary>Auto-discovered global plugins dir; kcap installs <see cref="KcapPlugin"/> here.</summary>
    public static string PluginsDir(string? home = null, string? xdgConfigHome = null) => Path.Combine(ConfigDir(home, xdgConfigHome: xdgConfigHome), "plugins");

    public static string KcapPlugin(string? home = null, string? xdgConfigHome = null) => Path.Combine(PluginsDir(home, xdgConfigHome), "kcap.ts");

    /// <summary>Marker recording the installed plugin version (sibling of kcap.ts).</summary>
    public static string KcapPluginMarker(string? home = null, string? xdgConfigHome = null) =>
        Path.Combine(PluginsDir(home, xdgConfigHome), ".kcap-extension-version");

    /// <summary>
    /// OpenCode's config file (<c>~/.config/opencode/opencode.json</c>), where kcap registers its
    /// MCP servers under the <c>mcp</c> block (see <see cref="Mcp.McpConfigShape.OpenCode"/>).
    /// </summary>
    public static string McpConfigJson(string? home = null, string? configDir = null, string? xdgConfigHome = null) =>
        Path.Combine(ConfigDir(home, configDir, xdgConfigHome), "opencode.json");

    /// <summary>
    /// OpenCode's user-global agent-instructions file (<c>~/.config/opencode/AGENTS.md</c>), where
    /// kcap installs its marker-delimited steering block.
    /// </summary>
    public static string AgentsMd(string? home = null, string? configDir = null, string? xdgConfigHome = null) =>
        Path.Combine(ConfigDir(home, configDir, xdgConfigHome), "AGENTS.md");

    /// <summary>
    /// Detection by OpenCode's config or data dir presence — OpenCode creates one
    /// on first run. The binary name <c>opencode</c> can also be probed by callers
    /// via <c>AgentDetection.BinaryOnPath("opencode")</c>.
    /// </summary>
    public static bool IsInstalled(string? home = null, string? configDir = null, string? xdgConfigHome = null, string? xdgDataHome = null) =>
        Directory.Exists(ConfigDir(home, configDir, xdgConfigHome)) || Directory.Exists(DataDir(home, xdgDataHome));

    /// <summary>Pure variant of <see cref="ConfigDir"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>) — a null override means "not set", never falls back to
    /// a real <c>OPENCODE_CONFIG_DIR</c>/<c>XDG_CONFIG_HOME</c> process-env read.</summary>
    public static string ConfigDirPure(string? home, string? configDir, string? xdgConfigHome) {
        if (!string.IsNullOrWhiteSpace(configDir)) return configDir;
        if (!string.IsNullOrEmpty(xdgConfigHome)) return Path.Combine(xdgConfigHome, "opencode");
        return Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode");
    }

    /// <summary>Pure variant of <see cref="DataDir"/> — never falls back to a real
    /// <c>XDG_DATA_HOME</c> process-env read.</summary>
    public static string DataDirPure(string? home, string? xdgDataHome) {
        if (!string.IsNullOrEmpty(xdgDataHome)) return Path.Combine(xdgDataHome, "opencode");
        return Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode");
    }

    /// <summary>Pure variant of <see cref="IsInstalled"/> — never falls back to the real process
    /// environment for any of its overrides.</summary>
    public static bool IsInstalledPure(string? home, string? configDir, string? xdgConfigHome, string? xdgDataHome) =>
        Directory.Exists(ConfigDirPure(home, configDir, xdgConfigHome)) || Directory.Exists(DataDirPure(home, xdgDataHome));
}
