using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Harness.Antigravity;

/// <summary>
/// Filesystem layout for Google Antigravity. Antigravity is a
/// GUI IDE (VS Code fork, Windsurf/Codeium lineage) whose agent state lives under
/// the SHARED <c>~/.gemini</c> home in an <c>antigravity</c> subdir (so paths reuse
/// <see cref="GeminiPaths.Root"/> and honor <c>GEMINI_CLI_HOME</c>). Each conversation
/// has a per-conversation JSONL transcript under <c>brain/&lt;id&gt;/…/logs/</c> and a
/// SQLite <c>conversations/&lt;id&gt;.db</c> (token/model in its protobuf <c>gen_metadata</c>).
///
/// kcap ships as an Antigravity <b>plugin</b>: a directory under the GUI config root
/// (<c>~/.gemini/config/plugins/kcap/</c>) holding a required <c>plugin.json</c> marker
/// (without it the GUI never loads the dir) plus a <c>hooks.json</c> that registers the
/// kcap control hooks. Per-workspace installs live under <c>&lt;root&gt;/.agents/plugins/kcap/</c>.
/// ⚠️ The <c>~/.gemini/antigravity-cli/</c> dir is the <c>agy</c> CLI's config root — the
/// GUI does NOT read it, so installing hooks there (as #256 originally did) is invisible
/// to the running IDE (GUI re-test).
///
/// ⚠️ <c>~/.gemini</c> is shared with the Gemini CLI — <see cref="GeminiPaths.IsInstalled"/>
/// must require a Gemini-specific marker so an Antigravity-only home doesn't read as
/// a Gemini install.
/// </summary>
public static class AntigravityPaths {
    /// <summary>Antigravity data root: <c>&lt;gemini-root&gt;/antigravity</c>.</summary>
    public static string Root(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "antigravity");

    /// <summary>GUI config root the IDE reads plugins from: <c>&lt;gemini-root&gt;/config</c>.</summary>
    public static string GuiConfigRoot(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "config");

    /// <summary>The kcap capture plugin dir the GUI loads: <c>&lt;gui-config&gt;/plugins/kcap</c>.</summary>
    public static string PluginDir(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GuiConfigRoot(home, geminiCliHome), "plugins", AntigravityHooks.BlockName);

    /// <summary>MCP server config the Antigravity IDE reads: <c>&lt;gui-config&gt;/mcp_config.json</c>.
    /// This is Antigravity's OWN MCP file — NOT the Gemini CLI's <c>~/.gemini/settings.json</c> — with
    /// the plain <c>mcpServers</c> command/args/env shape (<c>McpConfigShape.Standard</c>).</summary>
    public static string McpConfigJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GuiConfigRoot(home, geminiCliHome), "mcp_config.json");

    /// <summary>The <c>agy</c> CLI's OWN config root: <c>&lt;gemini-root&gt;/antigravity-cli</c>. The GUI
    /// does NOT read it (see this type's header), so it is the wrong place for capture hooks and the
    /// right place for anything scoped to a CLI invocation.</summary>
    public static string CliConfigRoot(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "antigravity-cli");

    /// <summary>The <c>agy</c> CLI's settings file: <c>&lt;cli-config&gt;/settings.json</c>. A DIFFERENT
    /// file from both <see cref="McpConfigJson"/> (Antigravity's MCP server list) and the Gemini CLI's
    /// <c>~/.gemini/settings.json</c>. Holds the CLI's own preferences plus the <c>permissions.allow</c>
    /// rules headless (<c>agy -p</c>) runs are evaluated against — headless cannot prompt, so a tool
    /// with no matching allow-rule is auto-denied.</summary>
    public static string CliSettingsJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(CliConfigRoot(home, geminiCliHome), "settings.json");

    /// <summary>Global steering/context file the IDE loads: <c>&lt;gemini-root&gt;/GEMINI.md</c> —
    /// SHARED with the Gemini CLI (both hardcode <c>~/.gemini/GEMINI.md</c>), so kcap's single
    /// marker-delimited block serves both. Honors <c>GEMINI_CLI_HOME</c> via <see cref="GeminiPaths.Root"/>.</summary>
    public static string InstructionsMd(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "GEMINI.md");

    /// <summary>Global skills dir the IDE reads: <c>&lt;gemini-root&gt;/skills</c>. Antigravity does NOT
    /// read the agent-agnostic <c>~/.agents/skills</c>, so kcap installs its skills here instead.</summary>
    public static string SkillsDir(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "skills");

    /// <summary>
    /// The two product roots one <c>antigravity</c> vendor writes conversations under — the GUI's
    /// <see cref="Root"/> and the CLI's <see cref="CliConfigRoot"/> — in a fixed order (GUI first).
    /// Returned whether or not each exists; callers filter by presence. Import enumerates BOTH so an
    /// <c>agy</c> conversation is not invisible to <c>kcap import --antigravity</c>. Conversation ids
    /// are UUIDs, unique across roots, so a chain never spans roots (a child's <c>messages/</c> dir
    /// lives beside its parent under one root) and per-root processing needs no cross-root dedup.
    /// </summary>
    public static IReadOnlyList<string> BrainProductRoots(string? home = null, string? geminiCliHome = null)
        => [Root(home, geminiCliHome), CliConfigRoot(home, geminiCliHome)];

    /// <summary>Per-conversation "brain" dir under an EXPLICIT product root:
    /// <c>&lt;productRoot&gt;/brain/&lt;id&gt;</c>. The root-parameterized form the dual-root import
    /// resolves paths through; the convenience overloads below fix the root to the GUI's.</summary>
    public static string BrainDirUnder(string productRoot, string conversationId)
        => Path.Combine(productRoot, "brain", conversationId);

    /// <summary>Full JSONL transcript under an explicit product root.</summary>
    public static string TranscriptFullPathUnder(string productRoot, string conversationId)
        => Path.Combine(BrainDirUnder(productRoot, conversationId), ".system_generated", "logs", "transcript_full.jsonl");

    /// <summary>Inter-agent messages dir under an explicit product root.</summary>
    public static string MessagesDirUnder(string productRoot, string conversationId)
        => Path.Combine(BrainDirUnder(productRoot, conversationId), ".system_generated", "messages");

    /// <summary>Per-conversation "brain" dir: <c>&lt;root&gt;/brain/&lt;id&gt;</c> (GUI root).</summary>
    public static string BrainDir(string conversationId, string? home = null, string? geminiCliHome = null)
        => BrainDirUnder(Root(home, geminiCliHome), conversationId);

    /// <summary>Full JSONL transcript: <c>&lt;brain&gt;/.system_generated/logs/transcript_full.jsonl</c> (GUI root).</summary>
    public static string TranscriptFullPath(string conversationId, string? home = null, string? geminiCliHome = null)
        => TranscriptFullPathUnder(Root(home, geminiCliHome), conversationId);

    /// <summary>Inter-agent messages dir (child→parent linkage): <c>&lt;brain&gt;/.system_generated/messages</c> (GUI root).</summary>
    public static string MessagesDir(string conversationId, string? home = null, string? geminiCliHome = null)
        => MessagesDirUnder(Root(home, geminiCliHome), conversationId);

    /// <summary>Per-conversation SQLite db (protobuf gen_metadata → tokens/model): <c>&lt;root&gt;/conversations/&lt;id&gt;.db</c>.</summary>
    public static string ConversationDb(string conversationId, string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "conversations", $"{conversationId}.db");

    /// <summary>
    /// The gen_metadata db that is a sibling of a conversation's <c>transcript_full.jsonl</c>.
    /// Derives the real (dashed) conversation id from the transcript path — the brain-dir name
    /// — so callers holding only the transcript path (e.g. the watcher, which sees a canonical
    /// dashless session id) still resolve the correct <c>conversations/&lt;id&gt;.db</c>. Returns
    /// null when the path doesn't match the expected
    /// <c>&lt;root&gt;/brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c> shape.
    /// </summary>
    public static string? ConversationDbFromTranscript(string transcriptPath) {
        // Require the EXACT shape …/brain/<id>/.system_generated/logs/transcript_full.jsonl —
        // validate each segment so an unexpected path fails open (returns null) instead of
        // being mapped to a guessed <root>/conversations/<derived>.db.
        if (!string.Equals(Path.GetFileName(transcriptPath), "transcript_full.jsonl", StringComparison.Ordinal))
            return null;

        var logsDir = Path.GetDirectoryName(transcriptPath);                 // …/logs
        var sysGen  = Path.GetDirectoryName(logsDir);                        // …/.system_generated
        var convDir = Path.GetDirectoryName(sysGen);                         // …/<id>
        var brain   = Path.GetDirectoryName(convDir);                        // …/brain
        var root    = Path.GetDirectoryName(brain);                          // …/<root>
        if (convDir is null || brain is null || root is null) return null;

        if (!string.Equals(Path.GetFileName(logsDir), "logs",              StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetFileName(sysGen),  ".system_generated", StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetFileName(brain),   "brain",             StringComparison.Ordinal)) return null;

        var convId = Path.GetFileName(convDir);
        if (string.IsNullOrEmpty(convId)) return null;

        return Path.Combine(root, "conversations", $"{convId}.db");
    }

    /// <summary>Global hooks config the kcap plugin installs into: <c>&lt;plugin-dir&gt;/hooks.json</c>.</summary>
    public static string GlobalHooksJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(PluginDir(home, geminiCliHome), "hooks.json");

    /// <summary>Plugin manifest marker the GUI requires: <c>&lt;plugin-dir&gt;/plugin.json</c>.</summary>
    public static string GlobalPluginManifest(string? home = null, string? geminiCliHome = null)
        => Path.Combine(PluginDir(home, geminiCliHome), "plugin.json");

    /// <summary>Per-workspace plugin dir (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap</c>.</summary>
    public static string WorkspacePluginDir(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".agents", "plugins", AntigravityHooks.BlockName);

    /// <summary>Per-workspace hooks config (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap/hooks.json</c>.</summary>
    public static string WorkspaceHooksJson(string workspaceRoot)
        => Path.Combine(WorkspacePluginDir(workspaceRoot), "hooks.json");

    /// <summary>
    /// Detection by data-root presence — the GUI creates <c>~/.gemini/antigravity</c> and the
    /// <c>agy</c> CLI creates <c>~/.gemini/antigravity-cli</c>, each on first run. EITHER root means
    /// the product is present: they are one vendor (<c>antigravity</c>) over two surfaces sharing
    /// the same plugin/MCP config, so an <c>agy</c>-only machine — GUI root absent, CLI root present
    /// — must detect exactly as a GUI machine does, or the shared hooks plugin never installs and no
    /// downstream capture runs. Callers additionally PATH-probe <c>agy</c> for the fresh case where
    /// neither root exists yet (see <c>SetupCommand</c>).
    /// </summary>
    public static bool IsInstalled(string? home = null, string? geminiCliHome = null)
        => Directory.Exists(Root(home, geminiCliHome))
        || Directory.Exists(CliConfigRoot(home, geminiCliHome));

    /// <summary>Pure variant of <see cref="Root"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>) — built on <see cref="GeminiPaths.RootPure"/>, never
    /// falls back to a real <c>GEMINI_CLI_HOME</c> process-env read.</summary>
    public static string RootPure(string? home, string? geminiCliHome)
        => Path.Combine(GeminiPaths.RootPure(home, geminiCliHome), "antigravity");

    /// <summary>Pure variant of <see cref="CliConfigRoot"/> — see <see cref="RootPure"/>.</summary>
    public static string CliConfigRootPure(string? home, string? geminiCliHome)
        => Path.Combine(GeminiPaths.RootPure(home, geminiCliHome), "antigravity-cli");

    /// <summary>Pure variant of <see cref="IsInstalled"/> — never falls back to the real process
    /// environment for <c>GEMINI_CLI_HOME</c>.</summary>
    public static bool IsInstalledPure(string? home, string? geminiCliHome)
        => Directory.Exists(RootPure(home, geminiCliHome))
        || Directory.Exists(CliConfigRootPure(home, geminiCliHome));
}
