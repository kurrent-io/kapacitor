using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>
/// Unit tests for <see cref="OpenCodeSubagentDiscovery"/>: the nested
/// child-file convention the kcap plugin writes + the parent watcher scans, the agent-name
/// resolution, and the vendor-agnostic <c>/hooks/subagent-{start,stop}</c> payload shapes
/// (whose required-field set the server's HookBase enforces).
/// </summary>
public class OpenCodeSubagentDiscoveryTests {
    [Test]
    public async Task SubagentDir_IsNestedDirNamedAfterParent() {
        var dir = OpenCodeSubagentDiscovery.SubagentDir(
            Path.Combine("cache", "kcap", "opencode", "ses_parent.jsonl"));
        await Assert.That(dir).IsEqualTo(Path.Combine("cache", "kcap", "opencode", "ses_parent"));
    }

    [Test]
    public async Task EnumerateSubagentFiles_ReturnsChildFiles_ElseEmpty() {
        using var tmp = new TempDir();

        var lonely = tmp.CreateFile("ses_lonely.jsonl", "");
        await Assert.That(OpenCodeSubagentDiscovery.EnumerateSubagentFiles(lonely).Count).IsEqualTo(0);

        var parent = tmp.CreateFile("ses_parent.jsonl", "");
        var nested = Path.Combine(tmp.Path, "ses_parent");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "ses_child1.jsonl"), "");
        File.WriteAllText(Path.Combine(nested, "ses_child2.jsonl"), "");

        await Assert.That(OpenCodeSubagentDiscovery.EnumerateSubagentFiles(parent).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveAgentType_ReadsInfoAgent_ElseFallsBackToSubagent() {
        using var tmp = new TempDir();

        var withAgent = Path.Combine(tmp.Path, "c1.jsonl");
        File.WriteAllText(withAgent,
            "{\"info\":{\"role\":\"user\",\"id\":\"m1\"},\"parts\":[]}\n" +
            "{\"info\":{\"role\":\"assistant\",\"id\":\"m2\",\"agent\":\"general\"},\"parts\":[]}\n");
        await Assert.That(OpenCodeSubagentDiscovery.ResolveAgentType(withAgent)).IsEqualTo("general");

        var noAgent = tmp.CreateFile("c2.jsonl", "{\"info\":{\"role\":\"user\",\"id\":\"m1\"},\"parts\":[]}\n");
        await Assert.That(OpenCodeSubagentDiscovery.ResolveAgentType(noAgent)).IsEqualTo("subagent");
    }

    [Test]
    public async Task BuildStartPayload_CarriesRequiredHookBaseAndAgentFields() {
        var p = OpenCodeSubagentDiscovery.BuildStartPayload("ses_parent", "ses_child", "general", "/c/ses_child.jsonl");

        await Assert.That(p["hook_event_name"]!.GetValue<string>()).IsEqualTo("subagent_start");
        await Assert.That(p["session_id"]!.GetValue<string>()).IsEqualTo("ses_parent");
        await Assert.That(p["agent_id"]!.GetValue<string>()).IsEqualTo("ses_child");
        await Assert.That(p["agent_type"]!.GetValue<string>()).IsEqualTo("general");
        // HookBase requires these (non-null) too — a missing one 400s.
        await Assert.That(p.ContainsKey("transcript_path")).IsTrue();
        await Assert.That(p.ContainsKey("cwd")).IsTrue();
    }

    [Test]
    public async Task BuildStopPayload_CarriesEveryRequiredSubagentStopField() {
        var p = OpenCodeSubagentDiscovery.BuildStopPayload("ses_parent", "ses_child", "general", "/c/ses_child.jsonl");

        await Assert.That(p["hook_event_name"]!.GetValue<string>()).IsEqualTo("subagent_stop");
        // The full required set the server's SubagentStopHook enforces.
        foreach (var key in new[] {
                     "session_id", "agent_id", "agent_type", "transcript_path", "cwd",
                     "stop_hook_active", "agent_transcript_path", "last_assistant_message"
                 }) {
            await Assert.That(p.ContainsKey(key)).IsTrue();
        }
    }
}
