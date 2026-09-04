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
    string? RequesterDisplay,
    // Whether the agent's runtime emits a PTY the app can attach to
    // (IHostedAgentRuntime.EmitsTerminalOutput). Trailing + nullable so every
    // existing positional construction stays valid; null = older daemon,
    // unknown — the app falls back to its vendor heuristic. Always emitted:
    // false is a real value, not an absence.
    bool? HasTerminal = null,
    // The launch prompt's first non-blank line, truncated by the daemon (TitleFromPrompt) —
    // display text for session rows. Trailing + nullable: null = older daemon or no goal text.
    string? Title = null,
    // Where the daemon found the agent's own transcript (Claude's project .jsonl, Codex's
    // rollout), link-resolved. Trailing + nullable: null is "older daemon", "not found yet",
    // "no transcript for this runtime", or "found nothing before the agent exited" alike —
    // a client waits, it never distinguishes them.
    string? TranscriptPath = null,
    // The checkout root the agent runs in. RepoPath is the repository it belongs to, for every
    // agent: a primary runs in an owned worktree under it, a borrowed reviewer in the worktree it
    // borrowed. Trailing + nullable: null = older daemon, which a client renders as it did before.
    string? WorktreePath = null,
    // "owned" or "borrowed"; derived from BorrowedFrom so the two never disagree. Null = older daemon.
    string? WorkLocation = null,
    // The checkout root a borrowed reviewer reviews — for a runtime that needs its own snapshot
    // this differs from WorktreePath, and it is the node the reviewer belongs under. Null unless
    // borrowed.
    string? BorrowedFrom = null,
    // The session id the daemon reports to the server: discovered from the transcript for a PTY
    // vendor, taken from the handshake for an ACP one. Null is "older daemon", "not resolved yet"
    // or "no session for this runtime" alike — a client waits, it never distinguishes them.
    string? SessionId = null,
    // The branch of the checkout the agent runs in; null from an older daemon or a launch that
    // recorded none (a borrowed in-place checkout).
    string? Branch = null);

/// Wire tokens for <see cref="AgentStatusDto.WorkLocation"/>, compared literally by every
/// client, so they never change.
public static class WorkLocationText {
    public const string Owned    = "owned";
    public const string Borrowed = "borrowed";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonStatusDto))]
public partial class StatusIpcJsonContext : JsonSerializerContext;
