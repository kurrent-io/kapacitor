using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace kapacitor;

record TranscriptBatch {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    [JsonPropertyName("line_numbers")]
    public int[]? LineNumbers { get; init; }

    [JsonPropertyName("repository")]
    public RepositoryPayload? Repository { get; init; }
}

record ErrorEntry(
        string         SessionId,
        string?        SessionSlug,
        string?        AgentId,
        int            EventNumber,
        string?        ToolName,
        string         Error,
        DateTimeOffset Timestamp
    );

record RecapEntry(
        string         Type,
        string?        SessionId,
        string?        AgentId,
        string?        AgentType,
        string         Content,
        string?        FilePath,
        DateTimeOffset Timestamp
    );

record RepositoryPayload {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PrTitle { get; init; }

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("pr_head_ref")]
    public string? PrHeadRef { get; init; }
}

record GitCacheEntry {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("cached_at")]
    public DateTimeOffset CachedAt { get; init; }
}

class WatchState {
    public int                LinesProcessed     { get; set; }
    public RepositoryPayload? Repository         { get; set; }
    public RepositoryPayload? LastSentRepository { get; set; }
    public DateTimeOffset     LastRepoDetection  { get; set; }
    public bool               InitialTitleSent   { get; set; }
    public bool               TitleGenerated     { get; set; }
    public int                TitleAttempts      { get; set; }
    public bool               TitleInFlight      { get; set; }
    public string?            FirstUserText      { get; set; }
    public bool               FullFileScanDone   { get; set; }
    public string?            FirstAssistantText { get; set; }
    public int                EventCount         { get; set; }

    // Buffering: hold transcript lines until threshold is reached to avoid polluting
    // the server with short-lived sessions (e.g. <local-command-caveat> prompts)
    public List<string> BufferedLines       { get; } = [];
    public List<int>    BufferedLineNumbers { get; } = [];
    public int          LinesReadAhead      { get; set; } // file position while buffering
    public bool         ThresholdReached    { get; set; }

    public const int TranscriptThreshold = 10;
}

record SessionTitlePayload {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }
}

record WhatsDonePayload {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }
}

record RepoRecapEntry(
        string          SessionId,
        string?         Slug,
        string?         Title,
        DateTimeOffset  StartedAt,
        DateTimeOffset? EndedAt,
        string          Summary
    );

// ── Eval command types — see DEV-1433 ─────────────────────────────────────

// Response shape from GET /api/sessions/{id}/eval-context. Only the fields
// the CLI needs; the server emits more that we don't parse (agent tagging,
// per-tool truncation breakdown, etc.) and System.Text.Json ignores them.
record EvalContextEntry {
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }
}

record EvalContextCompactionSummary {
    [JsonPropertyName("threshold_bytes")]
    public required int ThresholdBytes { get; init; }

    [JsonPropertyName("entries")]
    public required int Entries { get; init; }

    [JsonPropertyName("tool_results_total")]
    public required int ToolResultsTotal { get; init; }

    [JsonPropertyName("tool_results_truncated")]
    public required int ToolResultsTruncated { get; init; }

    [JsonPropertyName("bytes_saved")]
    public required long BytesSaved { get; init; }
}

record EvalContextResult {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("session_chain")]
    public required List<string> SessionChain { get; init; }

    [JsonPropertyName("trace")]
    public required List<EvalContextEntry> Trace { get; init; }

    [JsonPropertyName("compaction")]
    public required EvalContextCompactionSummary Compaction { get; init; }
}

/// <summary>
/// Wire-format DTO for a single eval question served by
/// <c>GET /api/eval/questions</c>. Mirrors the shape of
/// <c>Kurrent.Capacitor.EvalQuestionMetadata.Question</c> on the server —
/// the CLI cannot reference the Shared library (standalone submodule),
/// so the shape is duplicated here.
/// </summary>
record EvalQuestionDto {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    // DEV-1486: server-owned flag that opts this question into tools-enabled
    // judging. Defaults to false so older servers that don't send the field
    // keep producing text-only judge runs.
    [JsonPropertyName("needs_tools")]
    public bool NeedsTools { get; init; }
}

// Per-question verdict returned by each judge invocation. Matches the server
// event shape in SessionMetadataEvents.cs. `Evidence` is optional — judges
// may omit it when there's nothing specific to quote.
record EvalQuestionVerdict {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("question_id")]
    public required string QuestionId { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("finding")]
    public required string Finding { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }

    // DEV-1486: tool-call count for tools-enabled judges. Null for text-only
    // questions. Populated from the claude CLI's num_turns field minus 1
    // (the final StructuredOutput turn). Shipped back to the server so the
    // dashboard can surface actual budget spent per question.
    [JsonPropertyName("tools_used")]
    public int? ToolsUsed { get; init; }
}

record EvalCategoryResult {
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("questions")]
    public List<EvalQuestionVerdict> Questions { get; init; } = [];
}

record EvalRetrospective {
    [JsonPropertyName("overall")]
    public required string OverallSummary { get; init; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; init; } = [];

    [JsonPropertyName("issues")]
    public List<string> Issues { get; init; } = [];

    [JsonPropertyName("suggestions")]
    public List<string> Suggestions { get; init; } = [];
}

// Cross-eval memory — DEV-1434 / DEV-1438. Judges may optionally emit a
// retain_fact when they spot a cross-cutting pattern; the CLI POSTs it to
// the session-scoped endpoint and the server derives repo scope from the
// session (facts live on JudgeFacts-repo-{repoHash}-{category} streams).
// Facts accumulated on the same repo by any team member are fetched at
// eval startup and injected into each judge's prompt as "known patterns".
record JudgeFactPayload {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }
}

record JudgeFact {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("retained_at")]
    public required DateTimeOffset RetainedAt { get; init; }
}

// Posted to POST /api/sessions/{id}/evals. The server fills evaluated_at.
record SessionEvalCompletedPayload {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("judge_model")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("categories")]
    public List<EvalCategoryResult> Categories { get; init; } = [];

    [JsonPropertyName("overall_score")]
    public required int OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("retrospective")]
    public EvalRetrospective? Retrospective { get; init; }
}

enum HistorySessionStatus { New, Partial, AlreadyLoaded }

class SessionMetadata {
    public string?         Cwd            { get; set; }
    public string?         Model          { get; set; }
    public string?         Slug           { get; set; }
    public string?         SessionId      { get; set; }
    public DateTimeOffset? FirstTimestamp { get; set; }
    public DateTimeOffset? LastTimestamp  { get; set; }
}

static partial class GitUrlParser {
    public static (string? Owner, string? RepoName) ParseRemoteUrl(string? url) {
        if (url is null) {
            return (null, null);
        }

        var sshMatch = SshRegex().Match(url);

        if (sshMatch.Success) {
            return (sshMatch.Groups["owner"].Value, sshMatch.Groups["repo"].Value);
        }

        var httpsMatch = HttpsRegex().Match(url);

        return httpsMatch.Success
            ? (httpsMatch.Groups["owner"].Value, httpsMatch.Groups["repo"].Value)
            : (null, null);
    }

    [GeneratedRegex(@"https?://[^/]+/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    [GeneratedRegex(@"git@[\w.-]+:(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshRegex();
}

record RepoEntry {
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("last_used")]
    public required DateTimeOffset LastUsed { get; init; }
}

[JsonSerializable(typeof(List<RecapEntry>))]
[JsonSerializable(typeof(List<RepoRecapEntry>))]
[JsonSerializable(typeof(EvalContextResult))]
[JsonSerializable(typeof(EvalQuestionDto))]
[JsonSerializable(typeof(EvalQuestionDto[]))]
[JsonSerializable(typeof(EvalQuestionVerdict))]
[JsonSerializable(typeof(IReadOnlyList<EvalQuestionVerdict>))]
[JsonSerializable(typeof(EvalRetrospective))]
[JsonSerializable(typeof(SessionEvalCompletedPayload))]
[JsonSerializable(typeof(JudgeFactPayload))]
[JsonSerializable(typeof(List<JudgeFact>))]
[JsonSerializable(typeof(List<ErrorEntry>))]
[JsonSerializable(typeof(RepositoryPayload))]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSerializable(typeof(TranscriptBatch))]
[JsonSerializable(typeof(SessionTitlePayload))]
[JsonSerializable(typeof(WhatsDonePayload))]
[JsonSerializable(typeof(Auth.StoredTokens))]
[JsonSerializable(typeof(Auth.AuthDiscoveryResponse))]
[JsonSerializable(typeof(Auth.TokenExchangeRequest))]
[JsonSerializable(typeof(Auth.TokenExchangeResponse))]
[JsonSerializable(typeof(Auth.RefreshTokenRequest))]
[JsonSerializable(typeof(Auth.GitHubDeviceCodeResponse))]
[JsonSerializable(typeof(Auth.GitHubTokenResponse))]
[JsonSerializable(typeof(Auth.Auth0TokenResponse))]
[JsonSerializable(typeof(Auth.Auth0IdTokenClaims))]
[JsonSerializable(typeof(Auth.ProxyConfigResponse))]
[JsonSerializable(typeof(Auth.DiscoveredTenant[]))]
[JsonSerializable(typeof(LaunchAgentCommand))]
[JsonSerializable(typeof(ReviewLaunchInfo))]
[JsonSerializable(typeof(LaunchKind))]
[JsonSerializable(typeof(FindRepoForRemoteRequest))]
[JsonSerializable(typeof(SendInputCommand))]
[JsonSerializable(typeof(ResizeTerminalCommand))]
[JsonSerializable(typeof(PrepareEvalCommand))]
[JsonSerializable(typeof(RunQuestionCommand))]
[JsonSerializable(typeof(FinalizeEvalCommand))]
[JsonSerializable(typeof(CancelEvalCommand))]
[JsonSerializable(typeof(PrepareResult))]
[JsonSerializable(typeof(QuestionResult))]
[JsonSerializable(typeof(FinalizeResult))]
[JsonSerializable(typeof(EvalStarted))]
[JsonSerializable(typeof(EvalQuestionStarted))]
[JsonSerializable(typeof(EvalQuestionCompleted))]
[JsonSerializable(typeof(EvalQuestionFailed))]
[JsonSerializable(typeof(EvalFinished))]
[JsonSerializable(typeof(EvalFailed))]
[JsonSerializable(typeof(EvalRetrospectiveStarted))]
[JsonSerializable(typeof(EvalRetrospectiveCompleted))]
[JsonSerializable(typeof(EvalRetrospectiveFailed))]
[JsonSerializable(typeof(DaemonConnect))]
[JsonSerializable(typeof(AgentRegistered))]
[JsonSerializable(typeof(AgentStatusChanged))]
[JsonSerializable(typeof(AgentUnregistered))]
[JsonSerializable(typeof(LaunchFailed))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(AgentRunStarted))]
[JsonSerializable(typeof(AgentRunStopped))]
[JsonSerializable(typeof(AgentRunHeartbeat))]
[JsonSerializable(typeof(PermissionDecision))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(RepoEntry[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
partial class KapacitorJsonContext : JsonSerializerContext;

/// <summary>
/// Decision returned by the server's <c>RequestPermission</c> SignalR hub method.
/// Mirrors <c>PermissionResponseEntry</c> on the server side. ApplyPermissions /
/// UpdatedInput are typed as <see cref="JsonElement"/> so the daemon can relay
/// them verbatim into Claude's hook decision payload without the server having
/// to know about the hook wire shape.
/// </summary>
public readonly record struct PermissionDecision(
        string       Behavior,
        JsonElement? ApplyPermissions,
        JsonElement? UpdatedInput
    );

/// <summary>Commands sent from the server to daemon clients via SignalR.</summary>
public readonly record struct LaunchAgentCommand(
        string             AgentId,
        string?            Prompt,
        string             Model,
        string?            Effort,
        string             RepoPath,
        string[]?          Tools,
        string[]?          AttachmentIds,
        LaunchKind         Kind    = LaunchKind.Default,
        ReviewLaunchInfo?  Review  = null,
        string?            BaseRef = null
    );

/// <summary>
/// Discriminator for daemon launch commands. <see cref="Default"/> preserves
/// the existing prompt-driven launch; <see cref="Review"/> uses
/// <see cref="ReviewLaunchInfo"/> + <c>BaseRef</c> to drive a hosted PR review.
/// </summary>
public enum LaunchKind {
    Default = 0,
    Review  = 1
}

public readonly record struct ReviewLaunchInfo(
        string Owner,
        string Repo,
        int    PrNumber
    );

/// <summary>
/// Server → daemon probe asking "which of these candidate paths are a local
/// checkout of <c>owner/repo</c>?". The daemon merges the candidates with its
/// own knowledge, walks each up to a git root, validates origin, and returns
/// the confirmed roots.
/// </summary>
public readonly record struct FindRepoForRemoteRequest(
        string   Owner,
        string   Repo,
        string[] CandidatePaths
    );

public readonly record struct SendInputCommand(
        string    AgentId,
        string    Text,
        string[]? AttachmentIds
    );

public readonly record struct ResizeTerminalCommand(
        string AgentId,
        int    Cols,
        int    Rows
    );

/// <summary>Commands sent from daemon clients to the server via SignalR.</summary>
public readonly record struct DaemonConnect(
        string   Name,
        string   Platform,
        string[] RepoPaths,
        int      MaxAgents,
        string[] LiveAgentIds
    );

public readonly record struct AgentRegistered(
        string  AgentId,
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath
    );

public readonly record struct AgentStatusChanged(
        string  AgentId,
        string  Status,
        string? SessionId
    );

public readonly record struct AgentUnregistered(string AgentId);

public readonly record struct LaunchFailed(
        string AgentId,
        string Reason
    );

public readonly record struct TerminalOutput(
        string AgentId,
        string Base64Data
    );

// ── Per-question eval dispatch (DEV-1463 PR 2) ────────────────────────────
// Plain PascalCase records — no [JsonPropertyName] attrs — so they round-trip
// via SignalR's default JSON protocol with the matching server-side records.
// Inner DTOs (EvalQuestionDto, EvalQuestionVerdict) carry their own snake_case
// [JsonPropertyName] attrs which agree on both ends (see server's
// EvalQuestionMetadata.Question and SessionMetadataEvents.EvalQuestionVerdict).

/// <summary>Server → daemon: prepare an eval run. Daemon fetches + caches context, returns counts.</summary>
internal readonly record struct PrepareEvalCommand(
        string                         EvalRunId,
        string                         SessionId,
        string                         Model,
        bool                           Chain,
        int?                           ThresholdBytes,
        IReadOnlyList<EvalQuestionDto> Questions
    );

/// <summary>Server → daemon: run a single judge question against the cached context.</summary>
internal readonly record struct RunQuestionCommand(
        string          EvalRunId,
        EvalQuestionDto Question,
        int             Index,
        int             Total
    );

/// <summary>Server → daemon: aggregate verdicts, run retrospective, persist final result.</summary>
internal readonly record struct FinalizeEvalCommand(
        string                             EvalRunId,
        IReadOnlyList<EvalQuestionVerdict> Verdicts,
        string                             Model
    );

/// <summary>Server → daemon: discard any cached context for this run (e.g. dashboard aborted).</summary>
internal readonly record struct CancelEvalCommand(string EvalRunId);

/// <summary>Daemon → server: prepare-phase result.</summary>
internal readonly record struct PrepareResult(
        bool    Success,
        string? Error,
        string? CanonicalSessionId,
        int     TraceEntries,
        int     TraceChars,
        int     ToolResultsTotal,
        int     ToolResultsTruncated,
        long    BytesSaved
    );

/// <summary>Daemon → server: per-question judge result.</summary>
internal readonly record struct QuestionResult(
        bool                 Success,
        EvalQuestionVerdict? Verdict,
        string?              Error,
        long                 InputTokens,
        long                 OutputTokens
    );

/// <summary>Daemon → server: finalize-phase result including the aggregate to persist.</summary>
internal readonly record struct FinalizeResult(
        bool                         Success,
        string?                      Error,
        SessionEvalCompletedPayload? Aggregate
    );

/// <summary>Daemon → server: eval has fetched context and is about to run the first judge.</summary>
public readonly record struct EvalStarted(
        string EvalRunId,
        string SessionId,
        string JudgeModel,
        int    TotalQuestions
    );

/// <summary>Daemon → server: a judge question started running. Emitted before each claude invocation so the dashboard can show which question is currently in flight even when earlier ones failed.</summary>
public readonly record struct EvalQuestionStarted(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId
    );

/// <summary>Daemon → server: a judge question completed with a verdict.</summary>
public readonly record struct EvalQuestionCompleted(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId,
        int    Score,
        string Verdict
    );

/// <summary>Daemon → server: a judge question failed (claude returned no/unparseable result, timed out, or emitted an out-of-range score). The overall eval continues to the next question.</summary>
public readonly record struct EvalQuestionFailed(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId,
        string Reason
    );

/// <summary>Daemon → server: eval run finished end-to-end and aggregate has been persisted.</summary>
public readonly record struct EvalFinished(
        string EvalRunId,
        string SessionId,
        int    OverallScore,
        string Summary
    );

/// <summary>Daemon → server: eval run failed before producing an aggregate.</summary>
public readonly record struct EvalFailed(
        string EvalRunId,
        string SessionId,
        string Reason
    );

/// <summary>Daemon → server: retrospective pass is about to start (all category judges have completed).</summary>
public readonly record struct EvalRetrospectiveStarted(
        string SessionId,
        string EvalRunId
    );

/// <summary>Daemon → server: retrospective pass produced a summary and has been folded into the aggregate.</summary>
public readonly record struct EvalRetrospectiveCompleted(
        string SessionId,
        string EvalRunId
    );

/// <summary>Daemon → server: retrospective pass failed; the aggregate is still persisted without a retrospective.</summary>
public readonly record struct EvalRetrospectiveFailed(
        string SessionId,
        string EvalRunId,
        string Reason
    );

/// <summary>Agent run events posted to the server HTTP API.</summary>
record AgentRunStarted(
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        string? WorktreePath
    );

record AgentRunStopped(
        string? Reason,
        int?    ExitCode
    );

record AgentRunHeartbeat(
        string? SessionId
    );
