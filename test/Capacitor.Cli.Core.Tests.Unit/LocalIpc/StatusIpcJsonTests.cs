using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// Exact-JSON pins for the DaemonStatus payload (spec §4.1): snake_case, every field always
/// emitted (absent = null, never omitted), field order as declared, ISO-8601 UTC created_at.
/// Plus the §5 forward-compat pin: unknown members deserialize without error.
/// </summary>
public class StatusIpcJsonTests {
    [Test]
    public async Task DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("main", "0.12.3", "https://tenant.example.com", "connected", 5, 1, 4242, "inst-abc"),
            [
                new AgentStatusDto(
                    "agent-abc123", "review-flow", "codex", "/Users/x/dev/repo", "Live",
                    "flow_1", "reviewer", "github:12345",
                    new DateTime(2026, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc), "gpt-5-codex",
                    "Ada Lovelace", Title: "Fix the flaky test"),
                new AgentStatusDto(
                    "agent-b", "agent", "claude", null, "Starting",
                    null, null, null,
                    new DateTime(2026, 8, 1, 12, 35, 0, DateTimeKind.Utc), null,
                    null),
            ]);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);

        await Assert.That(json).IsEqualTo(
            """{"daemon":{"name":"main","version":"0.12.3","server_url":"https://tenant.example.com","connection":"connected","max_agents":5,"active_agents":1,"pid":4242,"instance_id":"inst-abc","supported_vendors":null},"agents":[{"id":"agent-abc123","kind":"review-flow","vendor":"codex","repo_path":"/Users/x/dev/repo","status":"Live","flow_run_id":"flow_1","flow_role":"reviewer","requester":"github:12345","created_at":"2026-08-01T12:34:56.789Z","model":"gpt-5-codex","requester_display":"Ada Lovelace","has_terminal":null,"title":"Fix the flaky test","transcript_path":null},{"id":"agent-b","kind":"agent","vendor":"claude","repo_path":null,"status":"Starting","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T12:35:00Z","model":null,"requester_display":null,"has_terminal":null,"title":null,"transcript_path":null}]}""");
    }

    [Test]
    public async Task Unknown_members_in_a_payload_deserialize_without_error() {
        // §5 forward compat: an older client must survive a future additive field.
        var json =
            """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":0,"future_field":42},"agents":[{"id":"a","kind":"agent","vendor":"codex","repo_path":null,"status":"Live","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null,"future_field":{"x":1}}],"future_top":true}""";

        var dto = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto);

        await Assert.That(dto!.Daemon.Name).IsEqualTo("m");
        await Assert.That(dto.Agents[0].Id).IsEqualTo("a");
        await Assert.That(dto.Agents[0].Status).IsEqualTo("Live");
    }

    [Test]
    public async Task Old_agent_json_without_has_terminal_deserializes_to_null() {
        // Serialize a current DTO, strip the member, deserialize — the exact old-daemon shape.
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null);
        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"has_terminal\":[^,}]+", "");

        var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(back!.HasTerminal).IsNull();
    }

    [Test]
    public async Task Old_agent_json_without_title_deserializes_to_null() {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null);
        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"title\":[^,}]+", "");

        var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(back!.Title).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Has_terminal_serializes_present_and_never_omitted(bool value) {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null, HasTerminal: value);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(json).Contains($"\"has_terminal\":{value.ToString().ToLowerInvariant()}");
    }

    [Test]
    public async Task Null_has_terminal_still_emits_the_member() {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(json).Contains("\"has_terminal\":null");
    }

    [Test]
    public async Task Transcript_path_serializes_last_and_null_is_emitted() {
        var withPath = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), null, null,
            HasTerminal: true, Title: "t", TranscriptPath: "/home/u/.claude/projects/-repo/abc.jsonl");
        var without = withPath with { TranscriptPath = null };

        var json = JsonSerializer.Serialize(withPath, StatusIpcJsonContext.Default.AgentStatusDto);
        var jsonNull = JsonSerializer.Serialize(without, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(json).EndsWith(""","has_terminal":true,"title":"t","transcript_path":"/home/u/.claude/projects/-repo/abc.jsonl"}""");
        await Assert.That(jsonNull).EndsWith(""","has_terminal":true,"title":"t","transcript_path":null}""");
    }

    [Test]
    public async Task Old_agent_json_without_transcript_path_deserializes_to_null() {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null, TranscriptPath: "/x.jsonl");
        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"transcript_path\":[^,}]+", "");

        var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(back!.TranscriptPath).IsNull();
    }
}
