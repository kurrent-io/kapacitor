using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// Structural validity for DaemonStatus payloads (spec §4.1): every member StatusIpc.cs
/// declares non-nullable must be non-null, and agent ids must be non-whitespace and unique
/// (ordinal) — STJ source-gen does not enforce non-nullable members at runtime.
/// </summary>
public class DaemonStatusValidatorTests {
    static DaemonStatusDto? Parse(string json) =>
        JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto);

    const string ValidJson =
        """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":1},"agents":[{"id":"a1","kind":"agent","vendor":"codex","repo_path":null,"status":"Running","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]}""";

    [Test]
    public async Task A_fully_populated_snapshot_is_valid() {
        await Assert.That(DaemonStatusValidator.IsValid(Parse(ValidJson))).IsTrue();
    }

    [Test]
    [Arguments("null")]
    [Arguments("{}")]
    [Arguments("""{"daemon":null,"agents":null}""")]
    [Arguments("""{"daemon":{"name":"m","version":null,"server_url":"u","connection":"connected","max_agents":5,"active_agents":0},"agents":[]}""")]
    [Arguments("""{"daemon":{"name":"m","version":"1","server_url":"u","connection":null,"max_agents":5,"active_agents":0},"agents":[]}""")]
    public async Task Null_root_daemon_agents_or_daemon_leaf_fields_are_invalid(string json) {
        await Assert.That(DaemonStatusValidator.IsValid(Parse(json))).IsFalse();
    }

    [Test]
    [Arguments("""[null]""")]                                                             // null element
    [Arguments("""[{"id":null,"kind":"agent","vendor":"v","repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]""")]
    [Arguments("""[{"id":"  ","kind":"agent","vendor":"v","repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]""")]
    [Arguments("""[{"id":"a1","kind":null,"vendor":"v","repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]""")]
    [Arguments("""[{"id":"a1","kind":"agent","vendor":null,"repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]""")]
    [Arguments("""[{"id":"a1","kind":"agent","vendor":"v","repo_path":null,"status":null,"flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]""")]
    public async Task Invalid_agent_elements_are_invalid(string agentsJson) {
        var json =
            $$"""{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":0},"agents":{{agentsJson}}}""";
        await Assert.That(DaemonStatusValidator.IsValid(Parse(json))).IsFalse();
    }

    [Test]
    public async Task Duplicate_agent_ids_are_invalid() {
        // SourceCache keyed diffing has no unambiguous meaning under duplicate keys.
        var json =
            """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":0},"agents":[{"id":"a1","kind":"agent","vendor":"v","repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null},{"id":"a1","kind":"agent","vendor":"v","repo_path":null,"status":"S","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]}""";
        await Assert.That(DaemonStatusValidator.IsValid(Parse(json))).IsFalse();
    }

    [Test]
    public async Task Unknown_vocabulary_is_valid_as_long_as_non_null() {
        var json =
            """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"warp-drive","max_agents":5,"active_agents":0},"agents":[{"id":"a1","kind":"quantum","vendor":"v","repo_path":null,"status":"Transcending","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}]}""";
        await Assert.That(DaemonStatusValidator.IsValid(Parse(json))).IsTrue();
    }
}
