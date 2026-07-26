using System.Text.Json.Serialization;

namespace Capacitor.Cli.SessionStartMemory;

internal enum SessionStartHarness {
    Claude,
    Codex,
    Cursor,
    Copilot,
    Gemini,
    Kiro,
    Pi,
    OpenCode,
    Antigravity
}

internal enum SessionLifecycleReason { New, Resume, Reopen, Fork, Compact, RepeatedTurnCallback, Unknown }
internal enum SessionMemoryLifecycleDecision { EligibleWithLease, EligibleOneShot, IneligibleNoCommit, RetryLaterNoCommit }
internal enum SessionStartMemoryDisposition { Ready, CompleteWithoutContext, RetryableFailure }

internal sealed record SessionMemoryLifecycle(
    SessionStartHarness Harness,
    string SessionId,
    string? LifecycleInstanceId,
    bool IsTopLevel,
    bool ClassificationAuthoritative,
    SessionLifecycleReason Reason,
    bool CallbackMayRepeat);

internal sealed record SessionStartMemoryEntry(
    [property: JsonPropertyName("memory_id")] string? MemoryId,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("audience")] string? Audience,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("kind")] string? Kind);

internal sealed record SessionStartMemoryContextRequest(
    string BaseUrl,
    string? Cwd,
    bool Disabled,
    TimeSpan Budget,
    CancellationToken CancellationToken);

internal sealed record SessionStartMemoryContextResult(
    SessionStartMemoryDisposition Disposition,
    string? Fragment = null,
    TimeSpan? RetryAfter = null) {
    public static readonly SessionStartMemoryContextResult Empty = new(SessionStartMemoryDisposition.CompleteWithoutContext);
    public static readonly SessionStartMemoryContextResult Retry = new(SessionStartMemoryDisposition.RetryableFailure);
}

internal sealed record SessionStartMemoryScope(string? RepoHash, string? MachineTag);

internal interface ISessionStartMemoryScopeResolver {
    Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct);
}

internal sealed record SessionStartMemoryLeaseHandle(string Key, long Generation, string Token);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SessionStartMemoryStoreRecord(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("policy_version")] int PolicyVersion,
    [property: JsonPropertyName("fragment_version")] int FragmentVersion,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("attempt")] long Attempt,
    [property: JsonPropertyName("lease_expires_at")] DateTimeOffset? LeaseExpiresAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
    [property: JsonPropertyName("disposition")] string? Disposition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SessionStartMemoryStoreMetadata(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("last_sweep_at")] DateTimeOffset? LastSweepAt,
    [property: JsonPropertyName("last_processed_filename")] string? LastProcessedFilename);

internal static class SessionStartMemoryConstants {
    public const int SchemaVersion = 1;
    public const int PolicyVersion = 1;
    public const int FragmentVersion = 1;
    public const int MaxRecordBytes = 4096;
    public const int MaxMetadataBytes = 16384;
    public const int MaxResponseBytes = 256 * 1024;
    public const int MaxEntries = 200;
    public const int MaxFragmentBytes = 24 * 1024;
    public const int NormalRecordCap = 50_000;
    public const int TotalEntryCap = 55_000;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
}
