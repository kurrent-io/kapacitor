namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Filesystem layout for DeepSeek Harness (dsh), AI-2020. dsh is event-sourced: it
/// writes a per-session <c>session.jsonl</c> (its <c>SessionEvent</c> stream) on disk,
/// which the kcap watcher tails directly and <c>kcap import --dsh</c> replays — one
/// normalizer serves both feeds.
///
/// <para><b>TODO (AI-2020):</b> the exact on-disk root + per-session layout is a
/// dsh-side detail not yet confirmed. kcap honors <c>KCAP_DSH_HOME</c> as an override
/// and defaults to <c>~/.deepseek/harness</c> with a per-session
/// <c>sessions/&lt;id&gt;/session.jsonl</c> layout. Confirm against a real dsh install
/// and adjust the defaults here (the rest of the pipeline is location-agnostic).</para>
/// </summary>
public static class DshPaths {
    /// <summary>dsh's config/data root (<c>~/.deepseek/harness</c>). Relocated by <c>KCAP_DSH_HOME</c>.</summary>
    public static string ConfigRoot(string? home = null) {
        var dshHome = Environment.GetEnvironmentVariable("KCAP_DSH_HOME");
        if (!string.IsNullOrEmpty(dshHome)) return dshHome;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".deepseek", "harness");
    }

    /// <summary>Per-session store root (<c>&lt;root&gt;/sessions</c>); each session is a
    /// subdirectory <c>&lt;id&gt;/session.jsonl</c>.</summary>
    public static string SessionsDir(string? home = null) =>
        Path.Combine(ConfigRoot(home), "sessions");

    /// <summary>The event-stream log for a session id.</summary>
    public static string SessionJsonl(string sessionId, string? home = null) =>
        Path.Combine(SessionsDir(home), sessionId, "session.jsonl");

    /// <summary>dsh plugin dir; kcap installs its Cordis plugin here.</summary>
    public static string PluginsDir(string? home = null) =>
        Path.Combine(ConfigRoot(home), "plugins");

    /// <summary>kcap's installed dsh plugin file. TODO (AI-2020): confirm dsh's plugin
    /// file name/format and auto-discovery dir.</summary>
    public static string KcapPlugin(string? home = null) =>
        Path.Combine(PluginsDir(home), "kcap.dsh.js");

    /// <summary>Version marker beside the installed plugin (mirrors the OpenCode installer).</summary>
    public static string KcapPluginMarker(string? home = null) =>
        Path.Combine(PluginsDir(home), ".kcap-extension-version");

    /// <summary>Detection: the config tree exists (callers also OR
    /// <c>AgentDetector.IsInstalled("dsh")</c> for binary-name coverage).</summary>
    public static bool IsInstalled(string? home = null) => Directory.Exists(ConfigRoot(home));
}
