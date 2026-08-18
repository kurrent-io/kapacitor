using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// Covers the testable pieces of the Kiro installer. Transparent capture clones
/// the user's default agent (preserving tools — a minimal agent loses tool
/// access) via <c>kiro-cli agent create --from</c>, then injects kcap's hook and
/// flips <c>chat.defaultAgent</c>. The clone + set-default round-trip needs
/// kiro-cli on PATH (integration); here we cover the parts that don't: hook
/// injection into an already-cloned file, the marker's previous-default record,
/// removal, and detection/parsing.
/// </summary>
public class KiroHooksTests {
    [Test]
    public async Task inject_adds_agentspawn_hook_and_preserves_cloned_agent_fields() {
        using var tmp = new TempDir();
        var agentPath = tmp.PathTo("agents", "kcap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);

        // A cloned default agent: real tools/prompt + an empty hooks block.
        await File.WriteAllTextAsync(agentPath,
            """{"name":"kcap","prompt":"sys","tools":["fs_read","execute_bash"],"allowedTools":["fs_read"],"hooks":{}}""");

        var ok = PluginCommand.InjectKiroHooksIntoAgent(agentPath);
        await Assert.That(ok).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(agentPath))!.AsObject();

        foreach (var evt in KiroHooksParser.KiroHookEvents) {
            var entry = root["hooks"]!.AsObject()[evt]!.AsArray()[0]!.AsObject();
            await Assert.That(entry["command"]!.GetValue<string>()).IsEqualTo($"kcap hook --kiro --event {evt}");
        }

        // agentSpawn is the only subscribed event — see KiroHooksParser.
        await Assert.That(KiroHooksParser.KiroHookEvents).Contains("agentSpawn");
        await Assert.That(KiroHooksParser.KiroHookEvents.Contains("stop")).IsFalse();

        // The cloned tools/prompt MUST survive — that's the whole reason we clone
        // instead of writing a minimal agent (which would lose tool access).
        await Assert.That(root["tools"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(root["prompt"]!.GetValue<string>()).IsEqualTo("sys");
    }

    [Test]
    public async Task inject_is_idempotent() {
        using var tmp = new TempDir();
        var agentPath = tmp.PathTo("agents", "kcap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        await File.WriteAllTextAsync(agentPath, """{"name":"kcap","hooks":{}}""");

        await Assert.That(PluginCommand.InjectKiroHooksIntoAgent(agentPath)).IsTrue();
        await Assert.That(PluginCommand.InjectKiroHooksIntoAgent(agentPath)).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(agentPath))!.AsObject();
        // No duplicate entries piled up on re-run.
        await Assert.That(root["hooks"]!.AsObject()["agentSpawn"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task inject_missing_file_returns_false() {
        using var tmp = new TempDir();
        await Assert.That(PluginCommand.InjectKiroHooksIntoAgent(tmp.PathTo("nope.json"))).IsFalse();
    }

    [Test]
    public async Task marker_records_and_restores_previous_default() {
        using var tmp = new TempDir();
        var agentPath = tmp.PathTo("agents", "kcap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);

        KiroHooksInstaller.WriteMarker(agentPath, "kiro_default");
        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsTrue();
        await Assert.That(KiroHooksInstaller.ReadMarker(agentPath)).IsNotNull();
        await Assert.That(KiroHooksInstaller.ReadPreviousDefault(agentPath)).IsEqualTo("kiro_default");

        // A version-only marker (kcap was already the default) records no previous.
        KiroHooksInstaller.WriteMarker(agentPath);
        await Assert.That(KiroHooksInstaller.ReadPreviousDefault(agentPath)).IsNull();
    }

    [Test]
    public async Task remove_deletes_agent_file_and_marker() {
        using var tmp = new TempDir();
        var agentPath = tmp.PathTo("agents", "kcap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        await File.WriteAllTextAsync(agentPath, "{}");
        KiroHooksInstaller.WriteMarker(agentPath, "kiro_default");

        await Assert.That(PluginCommand.RemoveKiroHooks(agentPath)).IsTrue();
        await Assert.That(File.Exists(agentPath)).IsFalse();
        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsFalse();
        await Assert.That(KiroHooksInstaller.ReadMarker(agentPath)).IsNull();
    }

    [Test]
    public async Task remove_without_install_reports_nothing_removed() {
        using var tmp = new TempDir();
        var agentPath = tmp.PathTo("agents", "kcap.json");

        await Assert.That(PluginCommand.RemoveKiroHooks(agentPath)).IsFalse();
    }

    [Test]
    public async Task is_installed_detects_pre_marker_installs_from_file_content() {
        using var tmp = new TempDir();
        var agentsDir = tmp.PathTo("agents");
        var agentPath = Path.Combine(agentsDir, "kcap.json");

        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(agentPath,
            """{"name":"kcap","hooks":{"agentSpawn":[{"command":"kcap hook --kiro --event agentSpawn"}]}}""");

        await Assert.That(KiroHooksInstaller.IsInstalled(agentPath)).IsTrue();
    }

    [Test]
    public async Task parser_matches_kiro_command_only() {
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"kcap hook --kiro --event agentSpawn"}"""))).IsTrue();
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"kcap hook --copilot --event sessionStart"}"""))).IsFalse();
        await Assert.That(KiroHooksParser.EntryReferencesCapacitorKiroHook(
            JsonNode.Parse("""{"command":"/usr/local/bin/other"}"""))).IsFalse();
    }

    [Test]
    public async Task has_capacitor_hooks_for_requires_every_event() {
        var root = JsonNode.Parse(
            """{"name":"kcap","hooks":{"agentSpawn":[{"command":"kcap hook --kiro --event agentSpawn"}]}}""")!.AsObject();

        await Assert.That(KiroHooksParser.HasCapacitorHooksFor(root, KiroHooksParser.KiroHookEvents)).IsTrue();
        await Assert.That(KiroHooksParser.HasCapacitorHooksFor(root, ["agentSpawn", "someFutureEvent"])).IsFalse();
    }
}
