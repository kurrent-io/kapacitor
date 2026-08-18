using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorHooksParserTests {
    [Test]
    public async Task EntryReferencesCapacitorCursorHook_true_for_bare_command() {
        var entry = JsonNode.Parse("""{"command":"kcap hook --cursor"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_true_for_command_with_extra_flags() {
        var entry = JsonNode.Parse("""{"command":"kcap hook --cursor --debug"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_true_for_legacy_kapacitor_marker() {
        // Pre-rename installs wrote `kapacitor hook --cursor`; uninstall and
        // the upgrade-time refresh must still recognise them so they can be
        // rewritten / cleaned. Mirrors the Codex parser's legacy detection.
        var entry = JsonNode.Parse("""{"command":"kapacitor hook --cursor"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_false_for_third_party_command() {
        var entry = JsonNode.Parse("""{"command":"/usr/local/bin/other"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_false_for_null() {
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(null)).IsFalse();
    }

    [Test]
    public async Task HasCapacitorHooksFor_true_when_every_event_has_kcap_entry() {
        var root = JsonNode.Parse("""
            {"hooks": {
                "sessionStart": [{"command":"kcap hook --cursor"}],
                "sessionEnd":   [{"command":"kcap hook --cursor"}]
            }}
        """)!.AsObject();
        await Assert.That(CursorHooksParser.HasCapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsTrue();
    }

    [Test]
    public async Task HasCapacitorHooksFor_false_when_event_missing() {
        var root = JsonNode.Parse("""{"hooks": {"sessionStart": [{"command":"kcap hook --cursor"}]}}""")!.AsObject();
        await Assert.That(CursorHooksParser.HasCapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsFalse();
    }
}
