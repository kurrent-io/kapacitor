using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Harness.Antigravity;

/// <summary>
/// Filesystem layout for Google Antigravity. Antigravity is a
/// GUI IDE (VS Code fork, Windsurf/Codeium lineage) whose agent state lives under
/// the SHARED <c>~/.gemini</c> home in an <c>antigravity</c> subdir (so every path here hangs off
/// the injected <see cref="GeminiPaths"/>, GEMINI_CLI_HOME included). Each conversation
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
public sealed class AntigravityPaths(GeminiPaths gemini) {
    public AntigravityPaths(UserHome home, string? geminiCliHome) : this(new GeminiPaths(home, geminiCliHome)) { }

    /// <summary>Reads the one override Antigravity honours (Gemini's); the home comes from the caller.</summary>
    public static AntigravityPaths FromEnvironment(UserHome home) => new(GeminiPaths.FromEnvironment(home));

    /// <summary>Antigravity data root: <c>&lt;gemini-root&gt;/antigravity</c>.</summary>
    public string Root => Path.Combine(gemini.Root, "antigravity");

    /// <summary>GUI config root the IDE reads plugins from: <c>&lt;gemini-root&gt;/config</c>.</summary>
    public string GuiConfigRoot => Path.Combine(gemini.Root, "config");

    /// <summary>The kcap capture plugin dir the GUI loads: <c>&lt;gui-config&gt;/plugins/kcap</c>.</summary>
    public string PluginDir => Path.Combine(GuiConfigRoot, "plugins", AntigravityHooks.BlockName);

    /// <summary>MCP server config the Antigravity IDE reads: <c>&lt;gui-config&gt;/mcp_config.json</c>.
    /// This is Antigravity's OWN MCP file — NOT the Gemini CLI's <c>~/.gemini/settings.json</c> — with
    /// the plain <c>mcpServers</c> command/args/env shape (<c>McpConfigShape.Standard</c>).</summary>
    public string McpConfigJson => Path.Combine(GuiConfigRoot, "mcp_config.json");

    /// <summary>The <c>agy</c> CLI's OWN config root: <c>&lt;gemini-root&gt;/antigravity-cli</c>. The GUI
    /// does NOT read it (see this type's header), so it is the wrong place for capture hooks and the
    /// right place for anything scoped to a CLI invocation.</summary>
    public string CliConfigRoot => Path.Combine(gemini.Root, "antigravity-cli");

    /// <summary>The <c>agy</c> CLI's settings file: <c>&lt;cli-config&gt;/settings.json</c>. A DIFFERENT
    /// file from both <see cref="McpConfigJson"/> (Antigravity's MCP server list) and the Gemini CLI's
    /// <c>~/.gemini/settings.json</c>. Holds the CLI's own preferences plus the <c>permissions.allow</c>
    /// rules headless (<c>agy -p</c>) runs are evaluated against — headless cannot prompt, so a tool
    /// with no matching allow-rule is auto-denied.</summary>
    public string CliSettingsJson => Path.Combine(CliConfigRoot, "settings.json");

    /// <summary>Global steering/context file the IDE loads: <c>&lt;gemini-root&gt;/GEMINI.md</c> —
    /// SHARED with the Gemini CLI (both hardcode <c>~/.gemini/GEMINI.md</c>), so kcap's single
    /// marker-delimited block serves both.</summary>
    public string InstructionsMd => gemini.GeminiMd;

    /// <summary>Global skills dir the IDE reads: <c>&lt;gemini-root&gt;/skills</c>. Antigravity does NOT
    /// read the agent-agnostic <c>~/.agents/skills</c>, so kcap installs its skills here instead.</summary>
    public string SkillsDir => Path.Combine(gemini.Root, "skills");

    /// <summary>
    /// The two product roots one <c>antigravity</c> vendor writes conversations under — the GUI's
    /// <see cref="Root"/> and the CLI's <see cref="CliConfigRoot"/> — in a fixed order (GUI first).
    /// Returned whether or not each exists; callers filter by presence. Import enumerates BOTH so an
    /// <c>agy</c> conversation is not invisible to <c>kcap import --antigravity</c>. Conversation ids
    /// are UUIDs, unique across roots, so a chain never spans roots (a child's <c>messages/</c> dir
    /// lives beside its parent under one root) and per-root processing needs no cross-root dedup.
    /// </summary>
    public IReadOnlyList<string> BrainProductRoots => [Root, CliConfigRoot];

    /// <summary>Per-conversation "brain" dir: <c>&lt;root&gt;/brain/&lt;id&gt;</c> (GUI root).</summary>
    public string BrainDir(string conversationId) => BrainDirUnder(Root, conversationId);

    /// <summary>Full JSONL transcript: <c>&lt;brain&gt;/.system_generated/logs/transcript_full.jsonl</c> (GUI root).</summary>
    public string TranscriptFullPath(string conversationId) => TranscriptFullPathUnder(Root, conversationId);

    /// <summary>Inter-agent messages dir (child→parent linkage): <c>&lt;brain&gt;/.system_generated/messages</c> (GUI root).</summary>
    public string MessagesDir(string conversationId) => MessagesDirUnder(Root, conversationId);

    /// <summary>Per-conversation SQLite db (protobuf gen_metadata → tokens/model): <c>&lt;root&gt;/conversations/&lt;id&gt;.db</c>.</summary>
    public string ConversationDb(string conversationId)
        => Path.Combine(Root, "conversations", $"{conversationId}.db");

    /// <summary>Global hooks config the kcap plugin installs into: <c>&lt;plugin-dir&gt;/hooks.json</c>.</summary>
    public string GlobalHooksJson => Path.Combine(PluginDir, "hooks.json");

    /// <summary>Plugin manifest marker the GUI requires: <c>&lt;plugin-dir&gt;/plugin.json</c>.</summary>
    public string GlobalPluginManifest => Path.Combine(PluginDir, "plugin.json");

    /// <summary>
    /// Detection by data-root presence — the GUI creates <c>~/.gemini/antigravity</c> and the
    /// <c>agy</c> CLI creates <c>~/.gemini/antigravity-cli</c>, each on first run. EITHER root means
    /// the product is present: they are one vendor (<c>antigravity</c>) over two surfaces sharing
    /// the same plugin/MCP config, so an <c>agy</c>-only machine — GUI root absent, CLI root present
    /// — must detect exactly as a GUI machine does, or the shared hooks plugin never installs and no
    /// downstream capture runs. Callers additionally PATH-probe <c>agy</c> for the fresh case where
    /// neither root exists yet (see <c>SetupCommand</c>).
    /// </summary>
    public bool IsInstalled => Directory.Exists(Root) || Directory.Exists(CliConfigRoot);

    /// <summary>Per-conversation "brain" dir under an EXPLICIT product root:
    /// <c>&lt;productRoot&gt;/brain/&lt;id&gt;</c>. The root-parameterized form the dual-root import
    /// resolves paths through; the instance members above fix the root to the GUI's.</summary>
    public static string BrainDirUnder(string productRoot, string conversationId)
        => Path.Combine(productRoot, "brain", conversationId);

    /// <summary>Full JSONL transcript under an explicit product root.</summary>
    public static string TranscriptFullPathUnder(string productRoot, string conversationId)
        => Path.Combine(BrainDirUnder(productRoot, conversationId), ".system_generated", "logs", "transcript_full.jsonl");

    /// <summary>Inter-agent messages dir under an explicit product root.</summary>
    public static string MessagesDirUnder(string productRoot, string conversationId)
        => Path.Combine(BrainDirUnder(productRoot, conversationId), ".system_generated", "messages");

    /// <summary>Per-workspace plugin dir (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap</c>.</summary>
    public static string WorkspacePluginDir(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".agents", "plugins", AntigravityHooks.BlockName);

    /// <summary>Per-workspace hooks config (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap/hooks.json</c>.</summary>
    public static string WorkspaceHooksJson(string workspaceRoot)
        => Path.Combine(WorkspacePluginDir(workspaceRoot), "hooks.json");

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

}
