using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Unit tests for <see cref="AntigravityPaths"/>. Antigravity data lives
/// under the shared <c>~/.gemini</c> home in an <c>antigravity</c> subdir; paths are
/// asserted against the captured on-disk layout (spike).
/// </summary>
public class AntigravityPathsTests {
    const string P = "/fake/parent";
    static string GeminiRoot => Path.Combine(P, ".gemini");

    static AntigravityPaths Ags(string home, string? geminiCliHome) => new(new(home), geminiCliHome);

    [Test]
    public async Task Root_is_antigravity_under_gemini_home() {
        await Assert.That(Ags("/h", P).Root)
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity"));
    }

    // GUI re-test: the plugin must land under the GUI config root
    // (~/.gemini/config/plugins/kcap/), NOT the agy CLI dir (~/.gemini/antigravity-cli/),
    // which the running IDE never reads.
    [Test]
    public async Task PluginDir_and_GlobalHooksJson_and_manifest_are_under_gui_config() {
        var pluginDir = Path.Combine(GeminiRoot, "config", "plugins", "kcap");
        await Assert.That(Ags("/h", P).GuiConfigRoot)
            .IsEqualTo(Path.Combine(GeminiRoot, "config"));
        await Assert.That(Ags("/h", P).PluginDir)
            .IsEqualTo(pluginDir);
        await Assert.That(Ags("/h", P).GlobalHooksJson)
            .IsEqualTo(Path.Combine(pluginDir, "hooks.json"));
        await Assert.That(Ags("/h", P).GlobalPluginManifest)
            .IsEqualTo(Path.Combine(pluginDir, "plugin.json"));
    }

    // MCP config is Antigravity's OWN file under the GUI config root (NOT the Gemini CLI's
    // settings.json); the steering file + skills dir are SHARED with Gemini under ~/.gemini.
    [Test]
    public async Task McpConfigJson_is_under_gui_config() {
        await Assert.That(Ags("/h", P).McpConfigJson)
            .IsEqualTo(Path.Combine(GeminiRoot, "config", "mcp_config.json"));
    }

    [Test]
    public async Task InstructionsMd_is_the_shared_gemini_md() {
        await Assert.That(Ags("/h", P).InstructionsMd)
            .IsEqualTo(Path.Combine(GeminiRoot, "GEMINI.md"));
    }

    [Test]
    public async Task SkillsDir_is_gemini_skills_not_agents_skills() {
        await Assert.That(Ags("/h", P).SkillsDir)
            .IsEqualTo(Path.Combine(GeminiRoot, "skills"));
    }

    [Test]
    public async Task TranscriptFullPath_matches_captured_layout() {
        await Assert.That(Ags("/h", P).TranscriptFullPath("conv1"))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "brain", "conv1", ".system_generated", "logs", "transcript_full.jsonl"));
    }

    [Test]
    public async Task MessagesDir_and_ConversationDb() {
        await Assert.That(Ags("/h", P).MessagesDir("conv1"))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "brain", "conv1", ".system_generated", "messages"));
        await Assert.That(Ags("/h", P).ConversationDb("conv1"))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "conversations", "conv1.db"));
    }

    [Test]
    public async Task WorkspaceHooksJson_is_dot_agents_plugin_dir() {
        await Assert.That(AntigravityPaths.WorkspacePluginDir("/repo"))
            .IsEqualTo(Path.Combine("/repo", ".agents", "plugins", "kcap"));
        await Assert.That(AntigravityPaths.WorkspaceHooksJson("/repo"))
            .IsEqualTo(Path.Combine("/repo", ".agents", "plugins", "kcap", "hooks.json"));
    }

    // G2 gateway: EITHER product root means installed. The agy-only row (GUI absent, CLI present)
    // is the one this fix exists for — before it, that machine was undetected and nothing installed.
    [Test]
    [Arguments(false, false, false)] // neither root
    [Arguments(true,  false, true)]  // GUI only
    [Arguments(false, true,  true)]  // agy CLI only  <-- the gateway case
    [Arguments(true,  true,  true)]  // both
    public async Task IsInstalled_is_true_when_EITHER_product_root_exists(
            bool gui, bool cli, bool expected) {
        using var tmp = new TempDir();
        // geminiCliHome: "" forces home-based resolution (no env read).
        if (gui) tmp.CreateDir(".gemini", "antigravity");
        if (cli) tmp.CreateDir(".gemini", "antigravity-cli");

        await Assert.That(Ags(tmp.Path, "").IsInstalled).IsEqualTo(expected);
    }

    [Test]
    public async Task CliConfigRoot_is_antigravity_cli_under_gemini_home() {
        await Assert.That(Ags("/h", P).CliConfigRoot)
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity-cli"));
    }

    // Import enumerates both roots, GUI first. Order is fixed so the log lines and any
    // first-wins behaviour are deterministic.
    [Test]
    public async Task BrainProductRoots_are_gui_then_cli() {
        var roots = Ags("/h", P).BrainProductRoots;
        await Assert.That(roots.Count).IsEqualTo(2);
        await Assert.That(roots[0]).IsEqualTo(Path.Combine(GeminiRoot, "antigravity"));
        await Assert.That(roots[1]).IsEqualTo(Path.Combine(GeminiRoot, "antigravity-cli"));
    }

    // The root-explicit helpers must resolve under the given product root verbatim — this is what
    // lets a CLI-root conversation's transcript/messages resolve under antigravity-cli, not the GUI.
    [Test]
    public async Task Under_helpers_resolve_beneath_the_given_product_root() {
        var paths = Ags("/h", P);
        var cli = Path.Combine(GeminiRoot, "antigravity-cli");
        const string id = "abc-123";
        await Assert.That(paths.BrainDirUnder(cli, id))
            .IsEqualTo(Path.Combine(cli, "brain", id));
        await Assert.That(paths.TranscriptFullPathUnder(cli, id))
            .IsEqualTo(Path.Combine(cli, "brain", id, ".system_generated", "logs", "transcript_full.jsonl"));
        await Assert.That(paths.MessagesDirUnder(cli, id))
            .IsEqualTo(Path.Combine(cli, "brain", id, ".system_generated", "messages"));
    }

    // The GUI-fixed convenience overloads must equal the Under() form anchored at the GUI root —
    // so pre-existing callers (live capture-adjacent) are byte-identical after the refactor.
    [Test]
    public async Task GUI_overloads_equal_the_Under_form_at_the_GUI_root() {
        var paths = Ags("/h", P);
        var gui   = paths.Root;
        const string id = "abc-123";
        await Assert.That(paths.TranscriptFullPath(id)).IsEqualTo(paths.TranscriptFullPathUnder(gui, id));
        await Assert.That(paths.MessagesDir(id)).IsEqualTo(paths.MessagesDirUnder(gui, id));
    }

    // the watcher sees a dashless session id but must resolve the
    // real (dashed) conversation's sibling gen_metadata db from the transcript path.
    [Test]
    public async Task ConversationDbFromTranscript_resolves_the_sibling_db() {
        var transcript = Ags("/h", P).TranscriptFullPath("abc-123-def");
        // GetFullPath normalizes separators so the assertion isn't brittle across OSes:
        // ConversationDbFromTranscript walks up with GetDirectoryName (which canonicalizes
        // separators on Windows) while ConversationDb builds via Path.Combine — same file,
        // possibly different separator style in the raw string.
        await Assert.That(Path.GetFullPath(AntigravityPaths.ConversationDbFromTranscript(transcript)!))
            .IsEqualTo(Path.GetFullPath(Ags("/h", P).ConversationDb("abc-123-def")));
    }

    [Test]
    [Arguments("foo.jsonl")]                                                              // wrong filename, shallow
    [Arguments("/a/b/c/d/e/transcript_full.jsonl")]                                       // right file, wrong segments
    [Arguments("/root/brain/id/.system_generated/logs/other.jsonl")]                      // wrong filename
    [Arguments("/root/brain/id/.system_generated/notlogs/transcript_full.jsonl")]         // wrong "logs" segment
    [Arguments("/root/brain/id/wrong/logs/transcript_full.jsonl")]                        // wrong ".system_generated"
    [Arguments("/root/notbrain/id/.system_generated/logs/transcript_full.jsonl")]         // wrong "brain"
    public async Task ConversationDbFromTranscript_returns_null_for_an_unexpected_path(string path) {
        await Assert.That(AntigravityPaths.ConversationDbFromTranscript(path)).IsNull();
    }
}
