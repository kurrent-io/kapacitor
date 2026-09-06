using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class WireShapeTests {
    // The property name IS the wire contract: deserialize a captured server-shaped payload and
    // pin every field, so a rename on our side fails here before it fails against a live server.
    [Test]
    public async Task AgentInstanceRoundTripsSnakeCase() {
        const string json = """
        {"agent_id":"a1","session_id":"s1","status":"Running","prompt":"fix the bug","model":"m",
         "effort":"high","repo_path":"/work/repo","client_connected":true,
         "registered_at":"2026-09-06T10:00:00Z","repo_owner":"kurrent-io","repo_name":"kcap-cli",
         "repo_hash":"abc","pr_number":7,"pr_url":"https://x","pr_title":"t","failure_reason":null,
         "owner_user_id":"u1","visibility_mode":"private",
         "grants":[{"grant_type":"user","grantee_id":"g1","grantee_name":"G"}],
         "vendor":"claude","ended_at":null,"status_changed_at":"2026-09-06T10:05:00Z",
         "sandbox_policy":"sp","approval_policy":"ap","daemon_name":"work-mac","permission_preset":"pp"}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(dto.AgentId).IsEqualTo("a1");
        await Assert.That(dto.OwnerUserId).IsEqualTo("u1");
        await Assert.That(dto.DaemonName).IsEqualTo("work-mac");
        await Assert.That(dto.RepoOwner).IsEqualTo("kurrent-io");
        await Assert.That(dto.Grants![0].GranteeId).IsEqualTo("g1");

        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.AgentInstanceDto);
        await Assert.That(back).Contains("\"agent_id\"");
        await Assert.That(back).Contains("\"owner_user_id\"");
        await Assert.That(back).Contains("\"daemon_name\"");
    }

    [Test]
    public async Task DaemonInfoRoundTripsSnakeCase() {
        const string json = """
        {"name":"work-mac","platform":"osx","repo_paths":["/work/repo"],"max_agents":5,
         "active_agents":1,"connected":true,"connected_at":"2026-09-06T09:00:00Z",
         "owner_user_id":"u1","version":"1.2.3","supported_vendors":["claude","codex"],
         "machine_id":"m-abc","unattended_vendors":["codex"],"pr_review_vendors":null,
         "acp_preset_vendors":null,"permission_mode_vendors":["claude"]}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(dto.Name).IsEqualTo("work-mac");
        await Assert.That(dto.MachineId).IsEqualTo("m-abc");
        await Assert.That(dto.OwnerUserId).IsEqualTo("u1");
        await Assert.That(dto.SupportedVendors).IsEquivalentTo(new[] { "claude", "codex" });
        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.DaemonInfo);
        await Assert.That(back).Contains("\"machine_id\"");
        await Assert.That(back).Contains("\"owner_user_id\"");
    }

    [Test]
    public async Task UnknownServerFieldsAreIgnored() {
        const string json = """{"agent_id":"a1","status":"Running","brand_new_field":42}""";
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(dto.AgentId).IsEqualTo("a1");
    }

    [Test]
    public async Task PermissionResponseRouteEscapesBothIds() {
        var route = ApiRoutes.PermissionResponse("s/1", "r 2");
        await Assert.That(route).IsEqualTo("api/sessions/s%2F1/permission-response/r%202");
    }
}
