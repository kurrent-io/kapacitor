using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// Wire contract for kcap-web /api/signup/*. CapacitorJsonContext is globally
// SnakeCaseLower, so EVERY field needs an explicit camelCase [JsonPropertyName]
// or the request serializes as snake_case and kcap-web rejects it (400 invalid).

// POST /api/signup/provision request
public sealed record ProvisionRequest {
    [JsonPropertyName("orgName")] public required string OrgName { get; init; }
    [JsonPropertyName("slug")]    public required string Slug    { get; init; }
    [JsonPropertyName("tier")]    public string          Tier    { get; init; } = "free";

    // The per-auth-run correlation key (Telemetry/SetupJoin.cs). WhenWritingNull, not merely
    // nullable: this context sets no global DefaultIgnoreCondition, so a bare property would ship
    // "joinId":null on every opted-out run and every pre-key build. An absent key must be an absent
    // member, so the body stays exactly what it was.
    [JsonPropertyName("joinId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JoinId { get; init; }
}

// POST /api/signup/provision response (202/200/400/409 bodies unioned; fields optional)
public sealed record ProvisionResponse {
    [JsonPropertyName("slug")]        public string? Slug        { get; init; }
    [JsonPropertyName("state")]       public string? State       { get; init; }
    [JsonPropertyName("url")]         public string? Url         { get; init; }
    [JsonPropertyName("workosOrgId")] public string? WorkosOrgId { get; init; }
    [JsonPropertyName("reason")]      public string? Reason      { get; init; }
}

// GET /api/signup/availability response
public sealed record AvailabilityResponse {
    [JsonPropertyName("available")] public bool    Available { get; init; }
    [JsonPropertyName("reason")]    public string? Reason    { get; init; }
}

// GET /api/signup/status response
public sealed record StatusResponse {
    [JsonPropertyName("state")]       public string? State       { get; init; }
    [JsonPropertyName("url")]         public string? Url         { get; init; }
    [JsonPropertyName("workosOrgId")] public string? WorkosOrgId { get; init; }
}
