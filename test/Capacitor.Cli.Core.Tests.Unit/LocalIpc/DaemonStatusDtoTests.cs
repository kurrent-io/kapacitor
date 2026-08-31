using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// Phase B (D2): the additive daemon self-report DTOs round-trip through the source-gen
/// <see cref="CapacitorJsonContext"/>, and the new trailing fields on existing wire types stay
/// backward-compatible (an old server's JSON without them still deserializes).
/// </summary>
public class DaemonStatusDtoTests {
    [Test]
    public async Task DaemonStatusReport_roundtrips_through_source_gen_context() {
        var report = new DaemonStatusReport(
            ActiveCount: 2,
            LiveAgents:  [new LiveAgentInfo("a1", "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer")],
            Quarantined: [new QuarantinedAgentInfo("a2", "Default", DateTimeOffset.UtcNow)]);

        var json = JsonSerializer.Serialize(report, CapacitorJsonContext.Default.DaemonStatusReport);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonStatusReport);

        await Assert.That(back.ActiveCount).IsEqualTo(2);
        await Assert.That(back.LiveAgents).Count().IsEqualTo(1);
        await Assert.That(back.LiveAgents[0].FlowRunId).IsEqualTo("flow-1");
        await Assert.That(back.LiveAgents[0].FlowRole).IsEqualTo("reviewer");
        await Assert.That(back.Quarantined[0].Id).IsEqualTo("a2");
    }

    [Test]
    public async Task LaunchAgentCommand_without_flow_fields_roundtrips_with_nulls() {
        // An old server builds the command without the new trailing FlowRunId/FlowRole (they default
        // to null). STJ source-gen binds by name, so the additive trailing params can't break the
        // wire: a command serialized without them deserializes back with the required fields intact
        // and the new fields null.
        var cmd = new LaunchAgentCommand(
            AgentId: "a1", Prompt: null, Model: "default", Effort: null,
            RepoPath: "/r", Tools: null, AttachmentIds: null, Vendor: "codex");

        var json = JsonSerializer.Serialize(cmd, CapacitorJsonContext.Default.LaunchAgentCommand);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(back.AgentId).IsEqualTo("a1");
        await Assert.That(back.Vendor).IsEqualTo("codex");
        await Assert.That(back.FlowRunId).IsNull();
        await Assert.That(back.FlowRole).IsNull();
    }

    [Test]
    public async Task DaemonConnect_old_json_without_live_agents_still_deserializes() {
        const string oldJson = """
                               {"name":"tony","platform":"macOS","repoPaths":[],"maxAgents":5,"liveAgentIds":[]}
                               """;

        var connect = JsonSerializer.Deserialize(oldJson, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(connect.Name).IsEqualTo("tony");
        await Assert.That(connect.LiveAgents).IsNull();
    }

    [Test]
    public async Task Supported_vendors_round_trips() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("kcap-dev", "1.2.3", "https://example.test", "connected", 4, 1,
                Pid: 42, InstanceId: "abc", SupportedVendors: ["claude", "cursor"]),
            []);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(back.Daemon.SupportedVendors).IsEquivalentTo(new[] { "claude", "cursor" });
    }

    [Test]
    public async Task Snapshot_without_supported_vendors_deserializes_as_null() {
        const string json = """
            {"daemon":{"name":"kcap-dev","version":"1","server_url":"u","connection":"connected",
            "max_agents":4,"active_agents":0,"pid":1,"instance_id":"i"},"agents":[]}
            """;

        var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(back.Daemon.SupportedVendors).IsNull();
    }
}
