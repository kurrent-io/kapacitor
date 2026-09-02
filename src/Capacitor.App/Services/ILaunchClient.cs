using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// Model "", Effort null and PermissionMode null all mean "vendor default" — the wire's own
/// conventions (the server rejects a null model; the daemon treats whitespace as no request).
public sealed record LaunchRequest(
    string DaemonName, string RepoPath, string Vendor, string? Prompt,
    string Model = "", string? Effort = null, string? PermissionMode = null);

/// Unauthorized marks a server 401 — the caller routes it to sign-in instead of rendering the
/// raw transport message.
public sealed record LaunchOutcome(bool Started, string? AgentId, string? Error, bool Unauthorized = false);

/// Starting a session goes through the SERVER, not the local socket: the local Spawn frame
/// resolves against the daemon's PTY launchers (claude and codex only), while the server's
/// RequestLaunchAgentV2 reaches every vendor through the runtime factories.
public interface ILaunchClient {
    Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct);
}

/// The RequestLaunchAgentV2 hub argument. A concrete record, not an anonymous type: the app
/// serializes through source-generated contexts throughout (AppStateStore, KcapCli), and a
/// reflection dependency here would foreclose ever AOT-publishing it. Member names must match
/// LaunchAgentRequestV2's properties — the hub binds by name.
// Explicit snake_case on every member: the server applies PropertyNamingPolicy =
// SnakeCaseLower to all hub payloads (kcap-server JsonDefaults.ConfigureSignalRPayload), and
// LaunchHubJson.Configure sets the same policy here. The explicit names are what survive that
// policy whatever it is set to, and they put the wire contract in plain sight next to the
// server record each member must match — this file has already shipped one launch-breaking
// key-casing defect, so the names are pinned rather than derived.
public sealed record LaunchAgentRequestV2Payload {
    [JsonPropertyName("daemon_name")]           public required string   DaemonName          { get; init; }
    [JsonPropertyName("prompt")]                public          string?  Prompt              { get; init; }
    [JsonPropertyName("model")]                 public required string   Model               { get; init; }
    [JsonPropertyName("effort")]                public          string?  Effort              { get; init; }
    [JsonPropertyName("repo_path")]             public required string   RepoPath            { get; init; }
    [JsonPropertyName("tools")]                 public          string[]? Tools              { get; init; }
    [JsonPropertyName("attachment_ids")]        public          string[]? AttachmentIds      { get; init; }
    [JsonPropertyName("visibility")]            public          string?  Visibility          { get; init; }
    [JsonPropertyName("grants")]                public          object[]? Grants             { get; init; }
    [JsonPropertyName("vendor")]                public required string   Vendor              { get; init; }
    [JsonPropertyName("codex_posture")]         public          object?  CodexPosture        { get; init; }
    [JsonPropertyName("acp_permission_preset")] public          string?  AcpPermissionPreset { get; init; }
    // Omitted when null, so an unchosen mode leaves the payload an older server expects untouched.
    [JsonPropertyName("permission_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionMode { get; init; }
}

[JsonSerializable(typeof(LaunchAgentRequestV2Payload))]
public partial class LaunchJsonContext : JsonSerializerContext;

/// The RequestLaunchAgentV2 argument, split from the transport so its shape is testable.
public static class LaunchPayload {
    public static LaunchAgentRequestV2Payload For(LaunchRequest r) => new() {
        DaemonName = r.DaemonName,
        Prompt     = string.IsNullOrWhiteSpace(r.Prompt) ? null : r.Prompt,
        Model      = r.Model.Trim(),   // "" = vendor default; the server rejects null
        Effort     = string.IsNullOrWhiteSpace(r.Effort) ? null : r.Effort,
        RepoPath   = r.RepoPath,
        Vendor     = r.Vendor,
        PermissionMode = string.IsNullOrWhiteSpace(r.PermissionMode) ? null : r.PermissionMode,
    };
}
