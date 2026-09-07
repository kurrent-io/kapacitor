using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One connected daemon as the server's registry presents it (hub GetConnectedDaemons and HTTP
/// GET api/daemons). Daemon names are unique only per OwnerUserId; MachineId is null from a
/// daemon that predates it. Every vendor list is null-when-unknown, never empty-when-unknown.
public sealed record DaemonInfo {
    [JsonPropertyName("name")]                    public required string Name { get; init; }
    [JsonPropertyName("platform")]                public string? Platform { get; init; }
    [JsonPropertyName("repo_paths")]              public string[]? RepoPaths { get; init; }
    [JsonPropertyName("max_agents")]              public int MaxAgents { get; init; }
    [JsonPropertyName("active_agents")]           public int ActiveAgents { get; init; }
    [JsonPropertyName("connected")]               public bool Connected { get; init; }
    [JsonPropertyName("connected_at")]            public DateTime? ConnectedAt { get; init; }
    [JsonPropertyName("owner_user_id")]           public string? OwnerUserId { get; init; }
    [JsonPropertyName("version")]                 public string? Version { get; init; }
    [JsonPropertyName("supported_vendors")]       public string[]? SupportedVendors { get; init; }
    [JsonPropertyName("machine_id")]              public string? MachineId { get; init; }
    [JsonPropertyName("unattended_vendors")]      public string[]? UnattendedVendors { get; init; }
    [JsonPropertyName("pr_review_vendors")]       public string[]? PrReviewVendors { get; init; }
    [JsonPropertyName("acp_preset_vendors")]      public string[]? AcpPresetVendors { get; init; }
    [JsonPropertyName("permission_mode_vendors")] public string[]? PermissionModeVendors { get; init; }
}
