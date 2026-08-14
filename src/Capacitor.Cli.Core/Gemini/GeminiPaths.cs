namespace Capacitor.Cli.Core.Gemini;

/// <summary>
/// Filesystem layout for Google Gemini CLI state. Everything lives under a
/// single root: <c>$GEMINI_CLI_HOME/.gemini</c> when <c>GEMINI_CLI_HOME</c> is
/// set (it names the PARENT dir, not the .gemini dir itself), otherwise
/// <c>~/.gemini</c> on every OS. Note: <c>GEMINI_HOME</c> is NOT a real Gemini
/// CLI variable and is intentionally not honored. Unlike Copilot's dedicated
/// <c>hooks/kcap.json</c>, Gemini's hooks live in the SHARED <c>settings.json</c>
/// under a <c>hooks</c> key, so the installer must MERGE (see
/// <see cref="GeminiHooksParser"/>).
/// </summary>
public static class GeminiPaths {
    public static string Root(string? home = null, string? geminiCliHome = null) {
        geminiCliHome ??= Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");

        var baseDir = !string.IsNullOrWhiteSpace(geminiCliHome)
            ? geminiCliHome
            : home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(baseDir, ".gemini");   // GEMINI_CLI_HOME is the PARENT of .gemini
    }

    /// <summary>
    /// Detection by a Gemini-CLI-specific marker under <c>~/.gemini</c>. The bare
    /// root is NOT sufficient because <c>~/.gemini</c> is SHARED with Google
    /// Antigravity, which stores its state under <c>antigravity/</c> +
    /// <c>antigravity-cli/</c> — so an Antigravity-only home would otherwise falsely
    /// read as a Gemini install. Require one of the config/recording
    /// markers Gemini CLI creates that Antigravity does not: <c>settings.json</c>,
    /// <c>projects.json</c>, or the <c>tmp/</c> chat-recording dir. The binary name
    /// <c>gemini</c> is too generic to be the only signal, so callers that want a
    /// PATH probe OR this with <c>AgentDetection.BinaryOnPath("gemini")</c>
    /// (a fresh install whose markers aren't written yet is still caught there).
    /// </summary>
    public static bool IsInstalled(string? home = null, string? geminiCliHome = null) {
        var root = Root(home, geminiCliHome);
        if (!Directory.Exists(root)) return false;

        return File.Exists(Path.Combine(root, "settings.json"))
            || File.Exists(Path.Combine(root, "projects.json"))
            || Directory.Exists(Path.Combine(root, "tmp"));
    }

    /// <summary>Pure variant of <see cref="Root"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>, and <see cref="Antigravity.AntigravityPaths"/>'s own
    /// pure arm, which shares this root) — <paramref name="geminiCliHome"/> null means "not set",
    /// never falls back to a real <c>GEMINI_CLI_HOME</c> process-env read.</summary>
    public static string RootPure(string? home, string? geminiCliHome) {
        var baseDir = !string.IsNullOrWhiteSpace(geminiCliHome)
            ? geminiCliHome
            : home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, ".gemini");
    }

    /// <summary>Pure variant of <see cref="IsInstalled"/> — never falls back to the real process
    /// environment for <c>GEMINI_CLI_HOME</c>.</summary>
    public static bool IsInstalledPure(string? home, string? geminiCliHome) {
        var root = RootPure(home, geminiCliHome);
        if (!Directory.Exists(root)) return false;

        return File.Exists(Path.Combine(root, "settings.json"))
            || File.Exists(Path.Combine(root, "projects.json"))
            || Directory.Exists(Path.Combine(root, "tmp"));
    }

    /// <summary>
    /// Shared settings file (<c>~/.gemini/settings.json</c>) — holds user config
    /// plus the <c>hooks</c> block kcap merges into. NEVER overwrite wholesale.
    /// </summary>
    public static string SettingsJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "settings.json");

    /// <summary>
    /// Global context/memory file (<c>~/.gemini/GEMINI.md</c>) — Gemini CLI loads it for
    /// every project (top of the hierarchical GEMINI.md chain), so it is where kcap installs
    /// its steering-instructions block. A separate file from <see cref="SettingsJson"/>; honors
    /// <c>GEMINI_CLI_HOME</c> like the rest of the layout.
    /// </summary>
    public static string GeminiMd(string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "GEMINI.md");

    /// <summary>
    /// Per-project temporary state root: <c>~/.gemini/tmp/&lt;project&gt;/</c>.
    /// Chat recordings live under <c>chats/</c> within each project dir.
    /// </summary>
    public static string TmpDir(string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "tmp");

    /// <summary>Chat-recording directory for a project tmp dir: <c>&lt;tmp&gt;/&lt;project&gt;/chats</c>.</summary>
    public static string ChatsDir(string projectTmpDir)
        => Path.Combine(projectTmpDir, "chats");

    /// <summary>
    /// Nested subagent-recording directory for a parent session:
    /// <c>&lt;chats&gt;/&lt;parentSessionId&gt;/</c>. Gemini records each subagent's transcript
    /// here as <c>&lt;subId&gt;.jsonl</c> (subId = a fresh dashed UUID — the executor's agent
    /// id). A subagent that itself spawns subagents gets its OWN nested dir the same way —
    /// <c>&lt;chats&gt;/&lt;subId&gt;/&lt;grandSubId&gt;.jsonl</c> — so deeper invocations are
    /// discovered by recursing into each descendant's own directory, not by assuming a flat
    /// layout (see <see cref="GeminiSubagentDiscovery.EnumerateDescendantFiles"/>).
    /// <paramref name="parentSessionId"/> is the DASHED form from the parent transcript's
    /// header, matching the on-disk directory name.
    /// </summary>
    public static string SubagentDir(string chatsDir, string parentSessionId)
        => Path.Combine(chatsDir, parentSessionId);
}
