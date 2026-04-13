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
[JsonSerializable(typeof(EvalQuestionVerdict))]
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
[JsonSerializable(typeof(LaunchAgentCommand))]
[JsonSerializable(typeof(SendInputCommand))]
[JsonSerializable(typeof(ResizeTerminalCommand))]
[JsonSerializable(typeof(DaemonConnect))]
[JsonSerializable(typeof(AgentRegistered))]
[JsonSerializable(typeof(AgentStatusChanged))]
[JsonSerializable(typeof(AgentUnregistered))]
[JsonSerializable(typeof(LaunchFailed))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(AgentRunStarted))]
[JsonSerializable(typeof(AgentRunStopped))]
[JsonSerializable(typeof(AgentRunHeartbeat))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(RepoEntry[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
partial class KapacitorJsonContext : JsonSerializerContext;

/// <summary>Commands sent from the server to daemon clients via SignalR.</summary>
public readonly record struct LaunchAgentCommand(
        string    AgentId,
        string?   Prompt,
        string    Model,
        string?   Effort,
        string    RepoPath,
        string[]? Tools,
        string[]? AttachmentIds
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
        int      MaxAgents
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
