using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class WireShapeTests {
    // The full [JsonPropertyName] vocabulary for each type, driven from an array so a missed
    // key on either side is a one-line addition rather than a second hand-written assertion.
    static readonly string[] AgentInstanceKeys = [
        "agent_id", "session_id", "status", "prompt", "model", "effort", "repo_path",
        "client_connected", "registered_at", "repo_owner", "repo_name", "repo_hash",
        "pr_number", "pr_url", "pr_title", "failure_reason", "owner_user_id",
        "visibility_mode", "grants", "vendor", "ended_at", "status_changed_at",
        "sandbox_policy", "approval_policy", "daemon_name", "permission_preset",
    ];

    static readonly string[] AccessGrantKeys = ["grant_type", "grantee_id", "grantee_name"];

    static readonly string[] DaemonInfoKeys = [
        "name", "platform", "repo_paths", "max_agents", "active_agents", "connected",
        "connected_at", "owner_user_id", "version", "supported_vendors", "machine_id",
        "unattended_vendors", "pr_review_vendors", "acp_preset_vendors", "permission_mode_vendors",
    ];

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
        // failure_reason is genuinely null on this fixture — the nullable-field representative,
        // proving Contains below still finds a key System.Text.Json serializes as `"key":null`.
        await Assert.That(dto.FailureReason).IsNull();

        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.AgentInstanceDto);
        foreach (var key in AgentInstanceKeys)
            await Assert.That(back).Contains($"\"{key}\"");
        foreach (var key in AccessGrantKeys)
            await Assert.That(back).Contains($"\"{key}\"");
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
        // pr_review_vendors is genuinely null on this fixture — the nullable-field representative.
        await Assert.That(dto.PrReviewVendors).IsNull();

        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.DaemonInfo);
        foreach (var key in DaemonInfoKeys)
            await Assert.That(back).Contains($"\"{key}\"");
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
