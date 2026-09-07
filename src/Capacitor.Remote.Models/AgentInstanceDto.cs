using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One agent instance as the server's UI-facing wire presents it (HTTP GET api/agent-instances
/// and hub payloads alike). Property NAMES are the contract on both transports — pinned
/// explicitly so no serializer policy can move them — and the server ignores members it does not
/// know, so additions here are always trailing and nullable.
public sealed record AgentInstanceDto {
    [JsonPropertyName("agent_id")]          public required string AgentId { get; init; }
    [JsonPropertyName("session_id")]        public string? SessionId { get; init; }
    [JsonPropertyName("status")]            public required string Status { get; init; }
    [JsonPropertyName("prompt")]            public string? Prompt { get; init; }
    [JsonPropertyName("model")]             public string? Model { get; init; }
    [JsonPropertyName("effort")]            public string? Effort { get; init; }
    [JsonPropertyName("repo_path")]         public string? RepoPath { get; init; }
    [JsonPropertyName("client_connected")]  public bool ClientConnected { get; init; }
    [JsonPropertyName("registered_at")]     public DateTime RegisteredAt { get; init; }
    [JsonPropertyName("repo_owner")]        public string? RepoOwner { get; init; }
    [JsonPropertyName("repo_name")]         public string? RepoName { get; init; }
    [JsonPropertyName("repo_hash")]         public string? RepoHash { get; init; }
    [JsonPropertyName("pr_number")]         public int? PrNumber { get; init; }
    [JsonPropertyName("pr_url")]            public string? PrUrl { get; init; }
    [JsonPropertyName("pr_title")]          public string? PrTitle { get; init; }
    [JsonPropertyName("failure_reason")]    public string? FailureReason { get; init; }
    [JsonPropertyName("owner_user_id")]     public string? OwnerUserId { get; init; }
    [JsonPropertyName("visibility_mode")]   public string? VisibilityMode { get; init; }
    [JsonPropertyName("grants")]            public AccessGrant[]? Grants { get; init; }
    [JsonPropertyName("vendor")]            public string? Vendor { get; init; }
    [JsonPropertyName("ended_at")]          public DateTime? EndedAt { get; init; }
    [JsonPropertyName("status_changed_at")] public DateTime? StatusChangedAt { get; init; }
    [JsonPropertyName("sandbox_policy")]    public string? SandboxPolicy { get; init; }
    [JsonPropertyName("approval_policy")]   public string? ApprovalPolicy { get; init; }
    [JsonPropertyName("daemon_name")]       public string? DaemonName { get; init; }
    [JsonPropertyName("permission_preset")] public string? PermissionPreset { get; init; }
}

public sealed record AccessGrant {
    [JsonPropertyName("grant_type")]   public required string GrantType { get; init; }
    [JsonPropertyName("grantee_id")]   public required string GranteeId { get; init; }
    [JsonPropertyName("grantee_name")] public required string GranteeName { get; init; }
}
