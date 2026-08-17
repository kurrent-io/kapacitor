using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

public class CursorHooksWriterTests {
    [Test]
    public async Task fresh_install_writes_all_eight_events() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        foreach (var evt in new[] {
            "sessionStart", "sessionEnd", "beforeSubmitPrompt",
            "afterAgentResponse", "afterAgentThought",
            "preToolUse", "postToolUse", "postToolUseFailure"
        }) {
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsGreaterThanOrEqualTo(1);
            var cmd = entries[0]!["command"]!.GetValue<string>();
            await Assert.That(cmd).IsEqualTo("kcap hook --cursor");
        }
    }

    [Test]
    public async Task install_preserves_user_authored_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"/usr/local/bin/other"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var start = root["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "/usr/local/bin/other")).IsTrue();
        await Assert.That(start.Any(e => e!["command"]!.GetValue<string>() == "kcap hook --cursor")).IsTrue();
    }

    [Test]
    public async Task install_replaces_existing_kcap_entries() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor --legacy"}]}}
        """);

        PluginCommand.InstallCursorHooks(hooksPath);

        var start = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!
            .AsObject()["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(start.Count(e => e!["command"]!.GetValue<string>().Contains("kcap hook --cursor")))
            .IsEqualTo(1);
        await Assert.That(start.Single(e => e!["command"]!.GetValue<string>().Contains("kcap hook --cursor"))!["command"]!.GetValue<string>())
            .IsEqualTo("kcap hook --cursor");
    }

    [Test]
    public async Task install_stamps_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kcap-hooks-version"))).IsTrue();
    }

    [Test]
    public async Task remove_strips_kcap_entries_and_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        PluginCommand.InstallCursorHooks(hooksPath);
        var removed = PluginCommand.RemoveCursorHooks(hooksPath);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, ".kcap-hooks-version"))).IsFalse();
    }
}
