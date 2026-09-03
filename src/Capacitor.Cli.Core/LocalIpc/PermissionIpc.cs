using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the permission control frames. snake_case on the wire; shared verbatim
/// by the daemon, the CLI, and the desktop app. Every member is always emitted (nulls written).
public sealed record PermissionPendingDto(
    string RequestId, string AgentId, string SessionId, string Vendor, string ToolName,
    JsonElement? ToolInput, JsonElement? Suggestions, bool ToolInputOmitted, bool SuggestionsOmitted,
    string RequestedAt, string? ToolUseId = null);

public sealed record PermissionResolveDto(
    string RequestId, string Decision, JsonElement? ApplyPermissions, JsonElement? UpdatedInput);

/// Outcome: allow|deny|withdrawn. Source: app|server|agent_gone|no_ui|daemon_shutdown.
public sealed record PermissionResolvedDto(string RequestId, string Outcome, string Source);

public sealed record PermissionAckDto(bool Ok, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PermissionPendingDto))]
[JsonSerializable(typeof(PermissionResolveDto))]
[JsonSerializable(typeof(PermissionResolvedDto))]
[JsonSerializable(typeof(PermissionAckDto))]
public partial class PermissionIpcJsonContext : JsonSerializerContext;

/// The bounds every caller-controlled value must satisfy before it rides a frame: the codec
/// rejects a frame over its cap and a rejected replay would kill every subscription forever.
public static class PermissionWire {
    public const int MaxToolNameBytes = 512;
    public const int MaxElementBytes  = 64 * 1024;
    public const int MaxAgentIdBytes  = 128;
    public const int MaxToolUseIdBytes = 128;

    /// A GUID in any case, with or without dashes, as "N"; null when the value is not a GUID.
    public static string? Canonical(string? id) =>
        !string.IsNullOrEmpty(id) && Guid.TryParse(id, out var g) ? g.ToString("N") : null;

    /// STJ source-gen leaves a missing member null and `{}` decodes fine; tool_name may be empty.
    public static bool IsPendingStructurallyValid(PermissionPendingDto? dto) =>
        dto is not null
        && !string.IsNullOrEmpty(dto.RequestId)
        && !string.IsNullOrEmpty(dto.AgentId)
        && !string.IsNullOrEmpty(dto.SessionId)
        && !string.IsNullOrEmpty(dto.Vendor)
        && dto.ToolName is not null
        && !string.IsNullOrEmpty(dto.RequestedAt);
}
