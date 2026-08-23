using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payload for the DaemonStatus frame. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Every field is ALWAYS emitted — absent values are
/// JSON null, never omitted (one wire shape, exact-JSON testable), so this context must never
/// gain a DefaultIgnoreCondition. Deserialization ignores unmapped members (STJ default) —
/// additive fields must never break an older client.
public sealed record DaemonStatusDto(DaemonInfoDto Daemon, List<AgentStatusDto> Agents);

/// <summary>
/// <see cref="Connection"/> ∈ connected|connecting|reconnecting|disconnected (lowercase).
/// <see cref="ActiveAgents"/> is derived from the SAME materialized agents array it ships
/// with (Status is "Starting" or "Running"), so count and array can never disagree within
/// one payload. <see cref="Pid"/>/<see cref="InstanceId"/> are additive trailing members
/// identifying the reporting daemon process for client-side correlation — always
/// populated by a current daemon (see the "every field ALWAYS emitted" rule above); null only
/// if an old snapshot from before this field existed were ever replayed.
/// </summary>
public sealed record DaemonInfoDto(
    string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents,
    int? Pid = null, string? InstanceId = null,
    // Vendor tokens this daemon can host, from the runtime factories' own availability probe —
    // the same set advertised to the server on DaemonConnect. Trailing/additive: null from a
    // daemon that predates it, which a client must read as UNKNOWN, never as "hosts nothing".
    string[]? SupportedVendors = null);

/// <summary>
/// <see cref="Status"/> is the daemon's internal status string VERBATIM (PascalCase, open
/// vocabulary — clients treat unknown values as opaque display text). <see cref="Kind"/>
/// uses the KindText wire spellings (agent/review/review-flow, unknown enum names pass
/// through) — one vocabulary across AgentList and this payload. <see cref="Requester"/> is
/// the opaque server-stamped requester id, null when unknown (old servers, local spawns).
/// <see cref="RequesterDisplay"/> is the server-stamped human-readable name for it, null on
/// an old server or a local spawn; choosing which of the two to render is presentation.
/// </summary>
public sealed record AgentStatusDto(
    string Id, string Kind, string Vendor, string? RepoPath, string Status,
    string? FlowRunId, string? FlowRole, string? Requester, DateTime CreatedAt, string? Model,
    string? RequesterDisplay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonStatusDto))]
public partial class StatusIpcJsonContext : JsonSerializerContext;
