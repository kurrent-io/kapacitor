using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexHooksParserTests {
    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_true_for_current_marker() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kcap hook --codex","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_true_for_pre_consolidation_marker() {
        // Installs from the `kcap codex-hook` era must still be recognised so
        // upgrade-time refresh and uninstall can rewrite / clean them.
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kcap codex-hook","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_true_for_legacy_kapacitor_marker() {
        // Pre-rename installs wrote `kapacitor codex-hook`; uninstall and the
        // upgrade-time refresh must still recognise them so they can be cleaned.
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kapacitor codex-hook","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_false_when_command_does_not_match() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"echo hi","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_false_for_null() {
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(null)).IsFalse();
    }

    [Test]
    public async Task HasCapacitorHooksFor_returns_true_when_all_events_have_kcap_entry() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasCapacitorHooksFor(root!, events)).IsTrue();
    }

    [Test]
    public async Task HasCapacitorHooksFor_returns_false_when_one_event_missing() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kcap codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"echo something else"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasCapacitorHooksFor(root!, events)).IsFalse();
    }

    [Test]
    public async Task CodexHookEvents_lists_all_six_events_in_canonical_order() {
        var expected = new[] {
            "SessionStart", "UserPromptSubmit", "PreToolUse",
            "PostToolUse", "PermissionRequest", "Stop"
        };

        await Assert.That(CodexHooksParser.CodexHookEvents.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++) {
            await Assert.That(CodexHooksParser.CodexHookEvents[i]).IsEqualTo(expected[i]);
        }
    }

    [Test]
    [Arguments("rm -rf / # kcap codex-hook")]     // marker buried in a shell comment
    [Arguments("evil kcap hook --codex")]          // marker is an argument, not the executable
    [Arguments("kcap-wrapper hook --codex")]       // executable is not kcap
    public async Task IsCapacitorCodexHookCommand_rejects_embedded_or_spoofed_marker(string command) {
        await Assert.That(CodexHooksParser.IsCapacitorCodexHookCommand(command)).IsFalse();
    }

    [Test]
    public async Task IsCapacitorCodexHookCommand_accepts_path_qualified_kcap() {
        await Assert.That(CodexHooksParser.IsCapacitorCodexHookCommand("/usr/local/bin/kcap hook --codex")).IsTrue();
        await Assert.That(CodexHooksParser.IsCapacitorCodexHookCommand("/opt/kcap codex-hook")).IsTrue();
    }
}
