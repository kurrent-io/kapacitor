using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

public sealed record LaunchRequest(string DaemonName, string RepoPath, string Vendor, string? Prompt);

public sealed record LaunchOutcome(bool Started, string? AgentId, string? Error);

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
public sealed record LaunchAgentRequestV2Payload {
    [JsonPropertyName("daemonName")]          public required string   DaemonName          { get; init; }
    [JsonPropertyName("prompt")]              public          string?  Prompt              { get; init; }
    [JsonPropertyName("model")]               public required string   Model               { get; init; }
    [JsonPropertyName("effort")]              public          string?  Effort              { get; init; }
    [JsonPropertyName("repoPath")]            public required string   RepoPath            { get; init; }
    [JsonPropertyName("tools")]               public          string[]? Tools              { get; init; }
    [JsonPropertyName("attachmentIds")]       public          string[]? AttachmentIds      { get; init; }
    [JsonPropertyName("visibility")]          public          string?  Visibility          { get; init; }
    [JsonPropertyName("grants")]              public          object[]? Grants             { get; init; }
    [JsonPropertyName("vendor")]              public required string   Vendor              { get; init; }
    [JsonPropertyName("codexPosture")]        public          object?  CodexPosture        { get; init; }
    [JsonPropertyName("acpPermissionPreset")] public          string?  AcpPermissionPreset { get; init; }
}

[JsonSerializable(typeof(LaunchAgentRequestV2Payload))]
public partial class LaunchJsonContext : JsonSerializerContext;

/// The RequestLaunchAgentV2 argument, split from the transport so its shape is testable.
public static class LaunchPayload {
    public static LaunchAgentRequestV2Payload For(LaunchRequest r) => new() {
        DaemonName = r.DaemonName,
        Prompt     = string.IsNullOrWhiteSpace(r.Prompt) ? null : r.Prompt,
        Model      = "",   // vendor default; the server rejects null
        RepoPath   = r.RepoPath,
        Vendor     = r.Vendor,
    };
}
