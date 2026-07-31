using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

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

    // Routes the server's INormalizerSelector to CodexNormalizer when "codex".
    // Null/absent → server treats the batch as Claude (default). Omitted on the
    // wire when null so older servers (pre-#576) keep deserialising the batch
    // unchanged — the server-side record had no vendor field before that PR.
    [JsonPropertyName("vendor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vendor { get; init; }

    // When true, the server returns non-2xx if any line in the batch fails to normalize
    // (HandleTranscript → 500 on batch.Strict && Failed>0), so a fail-closed importer aborts
    // instead of proceeding over a partially-ingested transcript. Omitted on the wire when
    // false so older servers keep deserialising unchanged.
    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Strict { get; init; }
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

    [JsonPropertyName("host")]
    public string? Host { get; init; }

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

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

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

    // Task 7: set by the shutdown final drain (isFinalDrain) when it held back an
    // unterminated/unparseable final line rather than consuming it. RunWatch reads it right after
    // the final drain to flag the session needs-import (never drop a truncated tail).
    public bool FinalDrainHeldIncompleteLine { get; set; }

    // Last wall-clock time new transcript content was observed on the rollout file.
    // Drives the Codex idle-timeout fallback (see WatchCommand.ShouldEndOnIdle).
    // Initialized when the watcher starts; updated in DrainNewLines on new lines.
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    // idle-clock freeze while disconnected. DisconnectedSince is set when the SignalR
    // connection drops and cleared when it returns; AccumulatedDisconnected sums the disconnected
    // durations SINCE the last transcript activity (reset to zero when LastActivityAt advances).
    // ShouldEndOnIdle subtracts it so a transient outage isn't counted as idleness, while a
    // genuinely idle session still ends after the configured CONNECTED-idle budget.
    public DateTimeOffset? DisconnectedSince       { get; set; }
    public TimeSpan        AccumulatedDisconnected { get; set; }

    // Tracks Codex tool-call call_ids that are currently in flight (started but
    // not yet finished). A function_call/custom_tool_call response_item adds the
    // call_id; its matching _output removes it. While this set is non-empty, the
    // idle-end check is suppressed: a long-running shell command or custom tool can
    // legitimately produce no new rollout lines for >60 min between its start and
    // output lines (the tool is still running). No hard ceiling: if the tool hangs
    // forever but the Codex process is still alive, the parent-exit watchdog will
    // eventually fire and end the session — adding a ceiling here would be YAGNI.
    public HashSet<string> PendingCodexToolCalls { get; } = new(StringComparer.Ordinal);

    // Highwater mark of the last Antigravity gen_metadata row already streamed as a
    // synthetic USAGE line, so the watcher only sends newly-appended cost rows on each
    // poll (server dedup by deterministic id is the backstop). -1 = none seen yet.
    public long LastAntigravityGenIdx { get; set; } = -1;

    // Most-recent Antigravity transcript step created_at, stamped onto synthetic USAGE lines
    // so their backfill event's recency reflects the turn, not the event-store write time.
    public string? LastAntigravityCreatedAt { get; set; }

    // Antigravity tool calls seen without a matching result step yet (PLANNER_RESPONSE
    // tool_calls increment; RUN_COMMAND/VIEW_FILE/LIST_DIRECTORY/CODE_ACTION decrement). A
    // long-running command produces no transcript line between its call and result, so this
    // suppresses the idle-timeout session-end while a tool is genuinely in flight (mirrors
    // the Codex PendingCodexToolCalls guard).
    public int PendingAntigravityToolCalls { get; set; }

    // Antigravity live subagent nesting: child conversation ids already POSTed to
    // /hooks/antigravity/subagent-link for this parent watcher. A child stays OUT of this set
    // until its link POST succeeds, so a failed POST retries on the next scan (fail-open).
    public HashSet<string> PostedSubagentLinks { get; } = new(StringComparer.Ordinal);

    // Task 10: Kiro turn anchors (the turn's final message_id) already streamed as a
    // synthetic KiroUsageBackfilled line, so a later drain never re-emits the same anchor. Mirrors
    // LastAntigravityGenIdx above, but keyed on the anchor string rather than a row index because
    // Kiro's sidecar has no stable ordinal — committed ONLY after a successful send (see
    // KiroUsagePendingAnchors).
    public HashSet<string> KiroUsageEmittedAnchors { get; } = new(StringComparer.Ordinal);

    // Anchors staged by the most recent AppendKiroUsageBackfillLines call but not yet committed
    // to KiroUsageEmittedAnchors. The watcher commits them into the set above only once the batch
    // carrying their synthetic lines lands; a failed send leaves this list to be recomputed (and
    // re-staged) fresh on the next drain, so nothing is lost.
    public List<string> KiroUsagePendingAnchors { get; } = [];

    // Task 11 (D0/D3) — byte offset (end of the last batch the runtime rewrite guard
    // verified and the server acked) the Cursor watcher's guard checks resume from each poll.
    // Distinct from LinesProcessed (a LINE-number cursor set from the server's acked frontier,
    // which can differ from the raw count of lines sent when a line was disposed differently
    // than "emitted"); this is a plain BYTE count so the guard can re-read/re-hash the exact
    // range it last verified. Only ever set for vendor == "cursor".
    public long CursorByteOffset { get; set; }

    // poll counter driving the periodic full-prefix re-hash cadence
    // (WatchCommand.CursorFullPrefixVerifyEveryNPolls). Incremented once per poll for vendor ==
    // "cursor" only; a plain counter (not wall-clock time) so the cadence is exact regardless of
    // how long any individual poll takes.
    public int CursorGuardPollCount { get; set; }

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
public record EvalContextEntry {
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }
}

public record EvalContextCompactionSummary {
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

public record EvalContextResult {
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
public record EvalQuestionDto {
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

    // Phase 3 — the catalog prompt version this question's rendered prompt
    // ran against. Null on the back-compat /api/eval/questions alias (which does
    // not emit it) and on older servers; populated only by /api/eval/catalog.
    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }

    // Phase 3 — RAW question text from the catalog, used by the tools path
    // (the embedded tools template substitutes this into {QUESTION_TEXT}). Null on
    // the alias / older servers. Distinct from Prompt, which on a reconciled
    // text-path question holds the server-RENDERED prompt.
    [JsonPropertyName("raw_text")]
    public string? RawText { get; init; }
}

/// <summary>
/// Wire-format DTO for <c>GET /api/eval/catalog</c>. Carries the
/// server-rendered retrospective prompt + its version, and the active questions
/// with raw text + server-rendered prompt + per-question prompt version +
/// needs_tools. There is NO top-level question template — the daemon uses each
/// question's rendered <c>Prompt</c> directly. The daemon fetches this once per
/// run and reconciles its run question list from it. Mirrors the server's Phase-2
/// EvalCatalogResponse shape (the CLI cannot reference the server library — shape
/// is duplicated).
/// </summary>
public record EvalCatalogDto {
    [JsonPropertyName("retrospective_prompt")]
    public required string RetrospectivePrompt { get; init; }

    [JsonPropertyName("retrospective_prompt_version")]
    public required string RetrospectivePromptVersion { get; init; }

    [JsonPropertyName("questions")]
    public List<EvalCatalogQuestionDto> Questions { get; init; } = [];
}

/// <summary>A single active question from <c>GET /api/eval/catalog</c>.</summary>
public record EvalCatalogQuestionDto {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("question_text")]
    public required string QuestionText { get; init; }   // RAW (tools path)

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }         // server-rendered (text path)

    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; init; }

    // SHOULD-FIX (round 2): `required` so a Phase-2 response that OMITS needs_tools
    // fails deserialization loudly rather than silently defaulting false (which would
    // route a tools question to the text path). System.Text.Json enforces `required`
    // members — a missing `needs_tools` throws JsonException. See the missing-field test.
    [JsonPropertyName("needs_tools")]
    public required bool NeedsTools { get; init; }
}

// Per-question verdict returned by each judge invocation. Matches the server
// event shape in SessionMetadataEvents.cs. `Evidence` is optional — judges
// may omit it when there's nothing specific to quote.
public record EvalQuestionVerdict {
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

    // Phase 3 — catalog prompt version stamped at aggregation time before
    // POSTing the V3 payload. Null until Aggregate fills it from the catalog.
    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }
}

public record EvalCategoryResult {
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("questions")]
    public List<EvalQuestionVerdict> Questions { get; init; } = [];
}

public record EvalRetrospective {
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

public record JudgeFact {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    // Nullable for backward compat with older servers that don't return this field.
    [JsonPropertyName("fact_hash")]
    public string? FactHash { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    // Nullable for backward compat with older servers that don't return this field.
    [JsonPropertyName("retainer_github_id")]
    public long? RetainerGitHubId { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("retained_at")]
    public required DateTimeOffset RetainedAt { get; init; }
}

// Snapshot of a judge fact at eval time. Sent in the facts_used field of
// SessionEvalCompletedPayload so the server can persist which facts were
// in scope when the eval ran, even if the live judge_facts pool is later
// modified (muted, deleted, replaced).
public record EvalFactSnapshotPayload {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact_hash")]
    public required string FactHash { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("retainer_github_id")]
    public required long RetainerGitHubId { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("retained_at")]
    public required DateTimeOffset RetainedAt { get; init; }
}

// Posted to POST /api/sessions/{id}/evals. The server fills evaluated_at.
public record SessionEvalCompletedPayload {
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

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
}

// V2 retrospective types — structured suggestions with audience tag.
// Mirror of server-side RetrospectiveSuggestion / EvalRetrospectiveV2 /
// SessionEvalCompletedPayloadV2 in Capacitor.Server.Core.
// Wire shape must stay 1:1 with the server so the V2 POST route deserializes
// correctly (snake_case field names enforced by [JsonPropertyName] below).

public record RetrospectiveSuggestion {
    [JsonPropertyName("text")]     public required string Text     { get; init; }
    [JsonPropertyName("audience")] public required string Audience { get; init; } // "agent" | "human"
}

public record EvalRetrospectiveV2 {
    // Backing fields coerce an explicit JSON `null` to an empty list so a
    // judge response like `"strengths": null` deserializes to an empty list
    // rather than a null field, keeping downstream code null-safe.

    [JsonPropertyName("overall")]
    public required string OverallSummary { get; init; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; init => field = value ?? []; } = [];

    [JsonPropertyName("issues")]
    public List<string> Issues { get; init => field = value ?? []; } = [];

    [JsonPropertyName("suggestions")]
    public List<RetrospectiveSuggestion> Suggestions { get; init => field = value ?? []; } = [];
}

// Posted to POST /api/sessions/{id}/evals/v2.
// Differs from SessionEvalCompletedPayload only in Retrospective type.
public record SessionEvalCompletedPayloadV2 {
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
    public EvalRetrospectiveV2? Retrospective { get; init; }

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
}

// Posted to POST /api/sessions/{id}/evals/v3. Differs from V2 by
// adding retrospective_prompt_version; the per-question version rides on each
// EvalQuestionVerdict.PromptVersion. Wire shape must stay 1:1 with the server's
// SessionEvalCompletedPayloadV3 in Capacitor.Server.
public record SessionEvalCompletedPayloadV3 {
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
    public EvalRetrospectiveV2? Retrospective { get; init; }

    [JsonPropertyName("retrospective_prompt_version")]
    public string? RetrospectivePromptVersion { get; init; }

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
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

        var sshProtoMatch = SshProtoRegex().Match(url);

        if (sshProtoMatch.Success) {
            return (sshProtoMatch.Groups["owner"].Value, sshProtoMatch.Groups["repo"].Value);
        }

        var httpsMatch = HttpsRegex().Match(url);

        return httpsMatch.Success
            ? (httpsMatch.Groups["owner"].Value, httpsMatch.Groups["repo"].Value)
            : (null, null);
    }

    // owner is greedy (`.+`) so a nested GitLab namespace (group/subgroup/...) is
    // captured whole, with repo as the final path segment. / §6b.
    [GeneratedRegex(@"https?://[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    // Anchored: a greedy multi-segment owner would otherwise let this match the
    // "git@host:port" inside an ssh:// URL and steal it from SshProtoRegex.
    [GeneratedRegex(@"^git@[\w.-]+:(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshRegex();

    [GeneratedRegex(@"ssh://(?:[^@/]+@)?[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshProtoRegex();
}

public record RepoEntry {
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("last_used")]
    public required DateTimeOffset LastUsed { get; init; }
}

// ── Projects (`kcap projects` / `kcap project <slug>`) — mirrors the server's
// ProjectSummaryDto / ProjectDetailDto (src/Capacitor.Server.Core/Projects/ProjectContracts.cs) ──

/// <summary>A single row from <c>GET /api/projects</c>.</summary>
public sealed record CliProjectSummary {
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("owner_user_id")]
    public required string OwnerUserId { get; init; }

    [JsonPropertyName("repo_count")]
    public int RepoCount { get; init; }

    [JsonPropertyName("member_count")]
    public int MemberCount { get; init; }

    /// <summary>"owner" | "member" | "none".</summary>
    [JsonPropertyName("viewer_membership")]
    public required string ViewerMembership { get; init; }

    /// <summary>"request" | "invite" | null.</summary>
    [JsonPropertyName("viewer_pending")]
    public string? ViewerPending { get; init; }

    [JsonPropertyName("pending_request_count")]
    public int PendingRequestCount { get; init; }

    [JsonPropertyName("repo_hashes")]
    public List<string> RepoHashes { get; init; } = [];
}

/// <summary>A repo entry inside <see cref="CliProjectDetail"/>.</summary>
public sealed record CliProjectRepo {
    [JsonPropertyName("repo_hash")]
    public required string RepoHash { get; init; }

    [JsonPropertyName("repo_slug")]
    public required string RepoSlug { get; init; }
}

/// <summary>A member entry inside <see cref="CliProjectDetail"/>.</summary>
public sealed record CliProjectMember {
    [JsonPropertyName("member_kind")]
    public required string MemberKind { get; init; }

    [JsonPropertyName("member_id")]
    public required string MemberId { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }
}

/// <summary>A pending join request/invite inside <see cref="CliProjectDetail"/>. Empty unless the viewer is owner/admin.</summary>
public sealed record CliProjectJoinRequest {
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    /// <summary>"request" | "invite".</summary>
    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    [JsonPropertyName("requested_at")]
    public DateTimeOffset RequestedAt { get; init; }
}

/// <summary>The body of <c>GET /api/projects/{slug}</c>.</summary>
public sealed record CliProjectDetail {
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("owner_user_id")]
    public required string OwnerUserId { get; init; }

    [JsonPropertyName("viewer_membership")]
    public required string ViewerMembership { get; init; }

    [JsonPropertyName("viewer_pending")]
    public string? ViewerPending { get; init; }

    [JsonPropertyName("repos")]
    public List<CliProjectRepo> Repos { get; init; } = [];

    [JsonPropertyName("members")]
    public List<CliProjectMember> Members { get; init; } = [];

    [JsonPropertyName("join_requests")]
    public List<CliProjectJoinRequest> JoinRequests { get; init; } = [];
}

/// <summary>Error body shared by every <c>/api/projects*</c> route on failure (e.g. <c>projects_not_in_plan</c>).</summary>
public sealed record CliProjectError {
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// One discovered plan/spec/design/checklist artifact returned by
/// <c>GET /api/sessions/{id}/plan-artifacts</c>. Mirrors the server's
/// <c>Capacitor.Plans.PlanArtifact</c> record field-for-field; string fields
/// (<see cref="Kind"/>, <see cref="Source"/>, <see cref="ContentState"/>,
/// <see cref="Confidence"/>) carry the server's snake_case enum values verbatim
/// (e.g. "plan", "repo_file", "truncated") — no local enum type, since the CLI
/// only needs to compare/display them, never branch on the full server vocabulary.
/// </summary>
public sealed record PlanArtifactDto {
    [JsonPropertyName("artifact_id")]     public required string ArtifactId { get; init; }
    [JsonPropertyName("kind")]            public required string Kind { get; init; }
    [JsonPropertyName("title")]           public required string Title { get; init; }
    [JsonPropertyName("source")]          public required string Source { get; init; }
    [JsonPropertyName("session_id")]      public required string SessionId { get; init; }
    [JsonPropertyName("agent_id")]        public string? AgentId { get; init; }
    [JsonPropertyName("agent_type")]      public string? AgentType { get; init; }
    [JsonPropertyName("head_session_id")] public string? HeadSessionId { get; init; }
    [JsonPropertyName("head_agent_id")]   public string? HeadAgentId { get; init; }
    [JsonPropertyName("head_agent_type")] public string? HeadAgentType { get; init; }
    [JsonPropertyName("head_discovered_at")]      public DateTimeOffset? HeadDiscoveredAt { get; init; }
    [JsonPropertyName("last_observed_session_id")] public string? LastObservedSessionId { get; init; }
    [JsonPropertyName("last_observed_at")] public DateTimeOffset? LastObservedAt { get; init; }
    [JsonPropertyName("path")]            public string? Path { get; init; }
    [JsonPropertyName("content")]         public string? Content { get; init; }
    [JsonPropertyName("content_state")]   public required string ContentState { get; init; } // "ok" | "truncated" | "unavailable"
    [JsonPropertyName("is_complete")]     public required bool IsComplete { get; init; }
    [JsonPropertyName("is_confirmed")]    public required bool IsConfirmed { get; init; }
    [JsonPropertyName("is_truncated")]    public bool IsTruncated { get; init; }
    [JsonPropertyName("original_bytes")]  public long? OriginalBytes { get; init; }
    [JsonPropertyName("content_hash")]    public required string ContentHash { get; init; }
    [JsonPropertyName("head_change_hash")] public string? HeadChangeHash { get; init; }
    [JsonPropertyName("version")]         public required int Version { get; init; }
    [JsonPropertyName("discovered_at")]   public required DateTimeOffset DiscoveredAt { get; init; }
    [JsonPropertyName("confidence")]      public required string Confidence { get; init; } // "high" | "medium" | "low"
    [JsonPropertyName("reason")]          public required string Reason { get; init; }
    [JsonPropertyName("is_primary")]      public bool IsPrimary { get; init; }
}

/// <summary>Body of <c>GET /api/sessions/{id}/plan-artifacts</c>.</summary>
public sealed record PlanArtifactsResponseDto {
    [JsonPropertyName("primary")]     public PlanArtifactDto? Primary { get; init; }
    [JsonPropertyName("artifacts")]   public List<PlanArtifactDto> Artifacts { get; init; } = [];
    [JsonPropertyName("diagnostics")] public List<string> Diagnostics { get; init; } = [];
}

public sealed record CurationApplyItem {
    [JsonPropertyName("category")]      public string?               Category     { get; init; }
    [JsonPropertyName("cluster_id")]    public string?               ClusterId    { get; init; }
    [JsonPropertyName("promoted_text")] public string?               PromotedText { get; init; }
    [JsonPropertyName("target_kinds")]  public IReadOnlyList<string>? TargetKinds { get; init; }
    [JsonPropertyName("status")]        public string?               Status       { get; init; }
}

public sealed record CurationApplyResponse {
    [JsonPropertyName("repo_hash")] public string?                      RepoHash { get; init; }
    [JsonPropertyName("items")]     public IReadOnlyList<CurationApplyItem>? Items { get; init; }
}

[JsonSerializable(typeof(List<RecapEntry>))]
[JsonSerializable(typeof(List<RepoRecapEntry>))]
[JsonSerializable(typeof(PlanArtifactDto))]
[JsonSerializable(typeof(PlanArtifactsResponseDto))]
[JsonSerializable(typeof(EvalContextResult))]
[JsonSerializable(typeof(EvalQuestionDto))]
[JsonSerializable(typeof(EvalQuestionDto[]))]
[JsonSerializable(typeof(EvalQuestionVerdict))]
[JsonSerializable(typeof(IReadOnlyList<EvalQuestionVerdict>))]
[JsonSerializable(typeof(EvalRetrospective))]
[JsonSerializable(typeof(SessionEvalCompletedPayload))]
[JsonSerializable(typeof(RetrospectiveSuggestion))]
[JsonSerializable(typeof(EvalRetrospectiveV2))]
[JsonSerializable(typeof(SessionEvalCompletedPayloadV2))]
[JsonSerializable(typeof(JudgeFactPayload))]
[JsonSerializable(typeof(List<JudgeFact>))]
[JsonSerializable(typeof(EvalFactSnapshotPayload))]
[JsonSerializable(typeof(List<EvalFactSnapshotPayload>))]
[JsonSerializable(typeof(EvalCatalogDto))]
[JsonSerializable(typeof(EvalCatalogQuestionDto))]
[JsonSerializable(typeof(SessionEvalCompletedPayloadV3))]
[JsonSerializable(typeof(List<ErrorEntry>))]
[JsonSerializable(typeof(List<CliProjectSummary>))]
[JsonSerializable(typeof(CliProjectDetail))]
[JsonSerializable(typeof(CliProjectError))]
[JsonSerializable(typeof(RepositoryPayload))]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSerializable(typeof(TranscriptBatch))]
[JsonSerializable(typeof(SessionTitlePayload))]
[JsonSerializable(typeof(WhatsDonePayload))]
[JsonSerializable(typeof(Auth.StoredTokens))]
[JsonSerializable(typeof(Auth.AuthDiscoveryResponse))]
[JsonSerializable(typeof(Auth.TokenExchangeRequest))]
[JsonSerializable(typeof(Auth.TokenExchangeResponse))]
[JsonSerializable(typeof(Auth.AuthErrorResponse))]
[JsonSerializable(typeof(Auth.RefreshTokenRequest))]
[JsonSerializable(typeof(Auth.GitHubDeviceCodeResponse))]
[JsonSerializable(typeof(Auth.GitHubTokenResponse))]
[JsonSerializable(typeof(Auth.GitHubCodeExchangeRequest))]
[JsonSerializable(typeof(Auth.WorkOSAuthResponse))]
[JsonSerializable(typeof(Auth.WorkOSUserInfo))]
[JsonSerializable(typeof(Auth.ProxyConfigResponse))]
[JsonSerializable(typeof(Auth.DiscoveredTenant[]))]
[JsonSerializable(typeof(LaunchAgentCommand))]
// Task 8: reviewer-model launch block + preflight RPC + resolved report wire DTOs.
[JsonSerializable(typeof(ExplicitReviewerModelLaunch))]
[JsonSerializable(typeof(ReviewerModelResolveRequestV1))]
[JsonSerializable(typeof(ReviewerModelResolveResponseV1))]
[JsonSerializable(typeof(ExplicitReviewerModelResolvedV1))]
[JsonSerializable(typeof(ReviewLaunchInfo))]
[JsonSerializable(typeof(LaunchKind))]
[JsonSerializable(typeof(FindRepoForRemoteRequest))]
[JsonSerializable(typeof(BorrowProbeResult))]
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
[JsonSerializable(typeof(UnattendedVendorCapability))]
[JsonSerializable(typeof(IReadOnlyList<UnattendedVendorCapability>))]
[JsonSerializable(typeof(LiveAgentInfo))]
[JsonSerializable(typeof(QuarantinedAgentInfo))]
[JsonSerializable(typeof(DaemonStatusReport))]
// Phase B2-b (sequenced-settlement design): report / connect side wire DTOs.
[JsonSerializable(typeof(ResolvedStartupCandidate))]
[JsonSerializable(typeof(ResolvedStartupCandidate[]))]
[JsonSerializable(typeof(UnresolvedStartupCandidate))]
[JsonSerializable(typeof(UnresolvedStartupCandidate[]))]
[JsonSerializable(typeof(StartupCandidateUnresolvedReason))]
[JsonSerializable(typeof(StartupDiscovery))]
[JsonSerializable(typeof(MarkerScanState))]
// Phase B2-b (sequenced-settlement design): command / ack side wire DTOs.
[JsonSerializable(typeof(StopAgentV2))]
[JsonSerializable(typeof(CommandAck))]
[JsonSerializable(typeof(CommandAckState))]
[JsonSerializable(typeof(CommandOutcomeKind))]
[JsonSerializable(typeof(AgentLiveness))]
[JsonSerializable(typeof(CommandRejected))]
[JsonSerializable(typeof(CommandRejectedReason))]
[JsonSerializable(typeof(AckProcessedPrefix))]
[JsonSerializable(typeof(ResolvedCandidateAck))]
[JsonSerializable(typeof(AckResolvedCandidates))]
[JsonSerializable(typeof(AgentPidRecord))]
[JsonSerializable(typeof(PidIdentityKind))]
[JsonSerializable(typeof(AgentRegistered))]
[JsonSerializable(typeof(AgentStatusChanged))]
[JsonSerializable(typeof(AgentUnregistered))]
[JsonSerializable(typeof(LaunchFailed))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(AgentRunStarted))]
[JsonSerializable(typeof(AgentRunStopped))]
[JsonSerializable(typeof(AgentRunHeartbeat))]
[JsonSerializable(typeof(PermissionDecision))]
[JsonSerializable(typeof(HostedPermissionRequest))]
[JsonSerializable(typeof(PermissionResolution))]
[JsonSerializable(typeof(EndAgentSessionResult))]
[JsonSerializable(typeof(MachineIdFile))]
[JsonSerializable(typeof(CursorQuarantineMarker))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(RepoEntry[]))]
[JsonSerializable(typeof(CurationApplyResponse))]
[JsonSerializable(typeof(CurationApplyItem))]
[JsonSerializable(typeof(Auth.ProvisionRequest))]
[JsonSerializable(typeof(Auth.ProvisionResponse))]
[JsonSerializable(typeof(Auth.AvailabilityResponse))]
[JsonSerializable(typeof(Auth.StatusResponse))]
[JsonSerializable(typeof(Acp.AcpRequest))]
[JsonSerializable(typeof(Acp.AcpResponse))]
[JsonSerializable(typeof(Acp.AcpNotification))]
[JsonSerializable(typeof(Acp.AcpError))]
[JsonSerializable(typeof(Acp.InitializeParams))]
[JsonSerializable(typeof(Acp.ClientCapabilities))]
[JsonSerializable(typeof(Acp.FsCapabilities))]
[JsonSerializable(typeof(Acp.InitializeResult))]
[JsonSerializable(typeof(Acp.AgentCapabilities))]
[JsonSerializable(typeof(Acp.SessionNewParams))]
[JsonSerializable(typeof(Acp.AcpMcpServerSpec))]
[JsonSerializable(typeof(Acp.AcpMcpServerEnvVar))]
[JsonSerializable(typeof(Acp.AcpMcpServerSpec[]))]
[JsonSerializable(typeof(Acp.SessionPromptParams))]
[JsonSerializable(typeof(Acp.PromptContentBlock))]
[JsonSerializable(typeof(Acp.SessionCancelParams))]
[JsonSerializable(typeof(Acp.SetConfigOptionParams))]
[JsonSerializable(typeof(Acp.SessionModelsInfo))]
[JsonSerializable(typeof(Acp.AvailableModelDto))]
[JsonSerializable(typeof(Acp.SessionRequestPermissionParams))]
[JsonSerializable(typeof(Acp.PermissionOptionDto))]
[JsonSerializable(typeof(Acp.PermissionOutcomeResult))]
[JsonSerializable(typeof(Acp.PermissionOutcomeDto))]
[JsonSerializable(typeof(Acp.ElicitationCreateParams))]
[JsonSerializable(typeof(Acp.ElicitationCreateResult))]
[JsonSerializable(typeof(AcpInteractionRequest))]
[JsonSerializable(typeof(AcpInteractionOption))]
[JsonSerializable(typeof(AcpInteractionDecision))]
[JsonSerializable(typeof(AcpInteractionResolution))]
[JsonSerializable(typeof(AcpEventEnvelope))]
[JsonSerializable(typeof(AcpEventEnvelope[]))]
[JsonSerializable(typeof(AcpBatchAck))]
[JsonSerializable(typeof(TranscriptBatchAck))]
// The AcpSessionStarted hub method's optional metadata argument. Registered as its own root type
// (not just nested inside another JsonSerializable graph) because SignalR's JsonHubProtocol
// serializes each hub-invocation argument independently by its declared type.
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
// UseStringEnumConverter=true matches the server's SignalR JSON protocol, which
// serialises enums (e.g. LaunchKind) as camelCase strings. Without it the
// source-gen LaunchKind JsonTypeInfo defaults to numeric and silently drops the
// invocation — the daemon receives "kind": "review" / "default" and the
// LaunchAgent handler never fires (DEV-1665).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true
)]
partial class CapacitorJsonContext : JsonSerializerContext;

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

/// <summary>
/// Single-argument payload for the <c>RequestPermission2</c> hub invocation. SignalR
/// binds hub-method arguments by count, so a record keeps the arity fixed at 1 and lets the wire
/// contract gain fields without breaking mixed-version servers. Mirrors the server-side record of
/// the same name in Capacitor.Server; property names must stay in sync (snake_case on the wire).
/// </summary>
public readonly record struct HostedPermissionRequest(
        string       SessionId,
        string?      ToolName,
        JsonElement? ToolInput,
        JsonElement? Suggestions
    );

/// <summary>
/// Payload of the <c>PermissionResolved</c> server→client push: the user's decision for a
/// hosted-agent permission request, correlated by <see cref="RequestId"/>. A single record (not
/// positional args) so the push contract can gain fields without breaking mixed-version daemons —
/// SignalR binds by argument count. Mirrors the server-side record of the same name.
/// </summary>
public readonly record struct PermissionResolution(
        string             RequestId,
        PermissionDecision Decision
    );

/// <summary>
/// Single-argument payload for the <c>AcpRequestInteraction</c> hub invocation. Mirrors
/// the server-side record of the same name in <c>Capacitor.Server.Core</c> (<c>src/Capacitor.Server.Core/AcpInteraction.cs</c>);
/// property names must stay in sync (snake_case on the wire via this context's naming policy).
/// <b>Spec-review Finding 1:</b> <see cref="RequestedSchema"/> is a new OPTIONAL trailing field,
/// mirroring the server-side <c>AcpInteractionRequest.RequestedSchema</c> exactly (same name,
/// position, and nullability) — kept in lockstep across the wire boundary the same way every other
/// field on this type already is (see Task A2's Interfaces note for the "server `record` / daemon
/// `readonly record struct`, same JSON shape" convention this type follows).
/// </summary>
public readonly record struct AcpInteractionRequest(
        string                 AgentId,
        string                 AcpSessionId,
        string                 Kind,
        string?                ToolName,
        JsonElement?           ToolInput,
        string?                ToolCallId,
        string?                Prompt,
        AcpInteractionOption[]? Options,
        bool                   IsMultiSelect,
        JsonElement?           RequestedSchema = null
    );

/// <summary>
/// One selectable option for an ACP permission or elicitation interaction. Spec-review
/// Finding 6: <see cref="OptionId"/> is the stable resolution key (mirrors
/// <c>Acp.PermissionOptionDto.OptionId</c>) — <see cref="Label"/> is display-only.
/// </summary>
public readonly record struct AcpInteractionOption(string OptionId, string Label, string? Description, string? Kind = null);

/// <summary>
/// Decision for an ACP interaction, pushed from the server. Mirrors the server-side
/// record of the same name. Spec-review Finding 6: <see cref="SelectedOptionId"/> is what
/// <c>AcpInteractionBridge.MapPermissionDecision</c> (Task B3) matches against — never
/// <see cref="SelectedOptionLabel"/>, which is retained for display/attribution only.
/// </summary>
public readonly record struct AcpInteractionDecision(
        string       Outcome,
        string?      SelectedOptionId,
        string?      SelectedOptionLabel,
        int?         SelectedIndex,
        string?      FreeText,
        JsonElement? UpdatedToolInput
    );

/// <summary>
/// Payload of the <c>AcpInteractionResolved</c> server→client push, correlated by
/// <see cref="RequestId"/>. Mirrors the server-side record of the same name.
/// </summary>
public readonly record struct AcpInteractionResolution(
        string             RequestId,
        AcpInteractionDecision Decision
    );

/// <summary>
/// Envelope-kind discriminator constants. Field-for-field mirror of the
/// server-side <c>Capacitor.Server.Core.Acp.AcpEventKind</c> static class (same constant names, same
/// wire string values) — kept as plain string constants (not a C# enum) because
/// <see cref="AcpEventEnvelope.Kind"/> itself is a plain <see langword="string"/> on both sides, not
/// an enum-backed field.
/// </summary>
public static class AcpEventKind {
    public const string SessionStarted     = "session_started";
    public const string UserMessage        = "user_message";
    public const string AssistantText      = "assistant_text";
    public const string AssistantThinking  = "assistant_thinking";
    public const string ToolCall           = "tool_call";
    public const string ToolResult         = "tool_result";
    public const string SessionTitle       = "session_title";
    public const string SessionEnded       = "session_ended";
    public const string Usage              = "usage";
}

/// <summary>
/// One canonical-equivalent event the daemon sends over the server's <c>AcpSessionEvents</c> hub
/// method. Daemon-local, field-for-field mirror of the server-side
/// <c>Capacitor.Server.Core.Acp.AcpEventEnvelope</c> record (same property names/types/defaults,
/// same "flat and Kind-discriminated, no polymorphism" shape) — read (never edited) from
/// <c>src/Capacitor.Server.Core/Acp/AcpEventEnvelope.cs</c> in the server repo. Neither
/// side declares an explicit <c>[JsonPropertyName]</c>: both ride the wire under a
/// <c>JsonNamingPolicy.SnakeCaseLower</c>-equivalent naming policy (the server's SignalR
/// <c>AddJsonProtocol</c> configuration; this context's <see cref="CapacitorJsonContext"/>'s
/// <see cref="JsonSourceGenerationOptionsAttribute.PropertyNamingPolicy"/>), so keeping the C#
/// property NAMES identical here is what keeps the wire shape identical — see
/// <c>AcpEventEnvelopeWireCompatTests</c> for the locked-in per-field wire-compat guard. Exactly one
/// per-kind field group is populated for a given <see cref="Kind"/> (see
/// <c>AcpEventTranslator.Translate</c>, which never sets a field outside its kind's group).
/// <c>Model</c> is the one exception: it is SHARED attribution metadata rather than a member of a
/// single kind's group — <c>session_started</c> carries it, and so does <c>usage</c>, because the
/// server's mapper is a pure per-envelope function with no session-fold access, so the resolved
/// model has to ride the wire on every reading that needs attribution.
/// </summary>
public readonly record struct AcpEventEnvelope(
        int     ContractVersion   = 1,
        long    Seq               = 0,
        string  Kind              = "",

        // text / thinking chunks
        string? Text              = null,
        bool    ThinkingEncrypted = false,

        // tool_call
        string? ToolCallId        = null,
        string? ToolName          = null,
        string? ToolInputJson     = null, // JSON object string

        // tool_result
        string? ToolResult        = null,
        bool    ToolIsError       = false,

        // session_started
        string? Model             = null,
        string? Cwd               = null,
        string? RawSessionId      = null,
        string? SessionMode       = null, // ACP session/new mode (agent|plan|ask)

        // session_ended
        string? EndReason         = null,

        // usage — context occupancy from the ACP Session Usage RFD. Additive and nullable, so
        // ContractVersion stays 1: an older server ignores them. The resolved model rides the
        // Model field above, stamped on every usage envelope.
        long?   ContextUsedTokens   = null,
        long?   ContextWindowTokens = null,

        // transcript-authoritative time (ISO-8601); server falls back to now if absent
        string? TimestampIso      = null
    );

/// <summary>
/// Ack returned from the server's <c>AcpSessionEvents</c> hub method.
/// Field-for-field mirror of the server-side <c>Capacitor.Server.Core.Acp.AcpBatchAck</c> record —
/// <see cref="ExpectedNextSeq"/> is set only on a gap-reject, telling the daemon where to rewind
/// (resend from <see cref="ExpectedNextSeq"/> on a gap; a terminal-drop ack
/// has <see cref="AcceptedSeq"/> below the daemon's max-sent seq AND a null
/// <see cref="ExpectedNextSeq"/>).
/// </summary>
public readonly record struct AcpBatchAck(long AcceptedSeq, long PersistedSeq, long? ExpectedNextSeq = null);

/// <summary>
/// Ack returned from the server's <c>SendTranscriptBatchAcked</c> hub method (D3).
/// Field-for-field mirror of the server-side <c>Capacitor.TranscriptBatchAck</c> record.
/// <see cref="NextLineNumber"/> is the source-acknowledgement frontier — the first line number
/// the server has NOT fully disposed of (emitted or deliberately ignored). The Cursor watcher
/// sets its local cursor from this value rather than the count of lines it sent, so a
/// server-held (retry-blocked or persist-blocked) line is re-delivered on the next poll and an
/// ignored (no-event) line still advances past — the server, not the client's send count, is
/// authoritative for what's actually been disposed of.
/// </summary>
public readonly record struct TranscriptBatchAck(int NextLineNumber);

/// <summary>Commands sent from the server to daemon clients via SignalR.</summary>
public readonly record struct LaunchAgentCommand(
        string            AgentId,
        string?           Prompt,
        string            Model,
        string?           Effort,
        string            RepoPath,
        string[]?         Tools,
        string[]?         AttachmentIds,
        string            Vendor,
        LaunchKind        Kind            = LaunchKind.Default,
        ReviewLaunchInfo? Review          = null,
        string?           BaseRef         = null,
        // D-c: for a review-flow launch, the flow definition's MCP allowlist — server-owned
        // names the daemon resolves against the kcap-owned KcapMcpRegistry and materializes into the
        // launcher's MCP config (flow-starting servers are stripped regardless of listing). Appended
        // last as an optional field so the SignalR positional binding stays wire-compatible with
        // older daemons/servers.
        string[]?         McpAllowlist = null,
        // Phase A: launch against the user's own checkout instead of a fresh daemon-owned
        // worktree. A bool on the wire (not the WorkLocation enum) — WorkLocation's numeric values
        // are BorrowedCwd=0/OwnedWorktree=1, the reverse of what you'd guess, so a raw enum int
        // would be a footgun; the daemon maps Borrowed -> WorkLocation internally. BorrowCwd is the
        // absolute path to borrow when Borrowed is true. Appended last, same wire-compat rule as the
        // fields above.
        bool               Borrowed = false,
        string?            BorrowCwd = null,
        // Phase B (D2): flow identity for a ReviewFlow launch, so the daemon can store it on
        // the AgentInstance and report it in LiveAgents / DaemonStatusReport (lets a restarted server
        // associate a surviving unassigned reviewer with its role). Appended last, same wire-compat
        // rule as the fields above — old daemons ignore them, old servers never set them.
        string?            FlowRunId = null,
        string?            FlowRole  = null,
        // Phase B2-b (sequenced-settlement design): additive per-command sequencing fields. Present
        // Epoch ⇒ the sequenced lane (the daemon serializes + watermarks against the shipped per-boot
        // _daemonEpoch); absent ⇒ the legacy unsequenced lane (never advances LastProcessedSeq).
        // CommandId is a GUID string. Appended last, same wire-compat rule as the fields above —
        // old daemons ignore them, old servers never set them.
        string?            Epoch     = null,
        long?              Seq       = null,
        string?            CommandId = null,
        // The server's vendor-specific unattended-review certification expectation.
        // Kept additive and optional so older servers and non-review launches remain compatible.
        ReviewerCertificationRequirement? ReviewerCertification = null,
        // Task 8: optional, versioned explicit reviewer-MODEL launch block. Non-null ONLY for a
        // protocol-v3 explicit-model reviewer launch the server drove through the daemon preflight;
        // null for every legacy/interactive launch (which keeps the ReportAgentResolvedModel path
        // unchanged). Appended last as an optional field so the SignalR positional/name binding stays
        // wire-compatible with older daemons (ignore it) and older servers (never set it). The daemon
        // launches with the exact LaunchModel VERBATIM and, post-launch, reports the concrete resolved
        // model back keyed on LaunchAttemptId (see ExplicitReviewerModelResolvedV1).
        ExplicitReviewerModelLaunch? ExplicitReviewerModel = null,
        // Interactive Codex launches only; any other launch shape is rejected by CodexPosturePolicy.
        // Appended last so the wire stays compatible with older daemons and servers.
        CodexLaunchPosture? CodexPosture = null,
        // AI-1623 consent: who asked for this launch. Appended last, same wire-compat rule as the
        // fields above — old daemons ignore them, old servers never set them (null ⇒ unknown ⇒
        // the consent engine falls through rules to the configured default).
        string?           RequesterUserId       = null,
        bool?             RequesterIsOwner      = null
    );

/// <summary>Caller-selected Codex launch posture. Valid ONLY for interactive, daemon-owned-worktree
/// launches (<see cref="LaunchKind.Default"/> and not borrowed); the daemon fails closed on any other
/// launch shape. Both fields are required — a partial block is malformed. Values are the Codex CLI's
/// own lowercase tokens, compared ordinally (see the daemon's CodexPosturePolicy).</summary>
public sealed record CodexLaunchPosture(string Sandbox, string Approval);

public sealed record ReviewerCertificationRequirement(
    string Vendor,
    string AllowedCliRanges,
    string RequiredLauncherPolicyVersion,
    string Revision,
    string ExpectedDaemonConnectionId,
    string ExpectedCliVersion);

/// <summary>Task 8: the server-pinned explicit reviewer-model launch parameters carried on
/// <see cref="LaunchAgentCommand.ExplicitReviewerModel"/>. <see cref="LaunchModel"/> is the EXACT model
/// the daemon must launch the reviewer with (threaded through verbatim — never recanonicalized);
/// <see cref="LaunchAttemptId"/> is the durable launch-attempt id the daemon must echo in its post-launch
/// <see cref="ExplicitReviewerModelResolvedV1"/> report so the server's one-shot waiter (keyed by
/// <c>(agentId, launchAttemptId)</c>) can rendezvous; <see cref="PolicyVersion"/> and
/// <see cref="EquivalenceKey"/> are the values the daemon's preflight already accepted, pinned so the
/// server can validate the report by equality. <see cref="ReportProtocolVersion"/> versions the report
/// contract (v1) additively.</summary>
public sealed record ExplicitReviewerModelLaunch(
    string LaunchAttemptId,
    string LaunchModel,
    string PolicyVersion,
    string EquivalenceKey,
    int    ReportProtocolVersion = 1);

// ── Task 8: reviewer-model preflight RPC + resolved report wire DTOs ──────────────────────
// These MATCH the server's DaemonCommands.cs shapes EXACTLY (field name/type/nullability) — the
// System.Text.Json snake_case binding is name-based, so a rename on either side silently breaks the
// wire. See kcap-server PR #1187 (Capacitor.Agents.ReviewerModelResolveRequestV1 / …ResponseV1 /
// ExplicitReviewerModelResolvedV1).

/// <summary>Server → daemon: "would <see cref="RequestedModel"/> launch under <see cref="Vendor"/>'s
/// current reviewer-model policy?" — a SIDE-EFFECT-FREE preflight (the daemon resolves, never spawns).
/// <see cref="RequestId"/> is the caller's launch-attempt id, echoed back verbatim so a stale/misrouted
/// reply is rejected; <see cref="ExpectedPolicyVersion"/> is the RPC PROTOCOL version
/// (<c>reviewer_model_resolve_v1</c>), echoed back so a protocol upgrade mid-flight is detected — it is
/// NOT the per-vendor resolver policy version (that is carried separately on the resolved report).</summary>
public sealed record ReviewerModelResolveRequestV1(
    string RequestId,
    string Vendor,
    string RequestedModel,
    string ExpectedPolicyVersion);

/// <summary>Daemon → server reply to <see cref="ReviewerModelResolveRequestV1"/>.
/// <see cref="Disposition"/> is exactly one of <c>"accepted"</c> / <c>"unavailable"</c> /
/// <c>"invalid"</c> (any other value the server treats as malformed → unavailable). On
/// <c>"accepted"</c>, <see cref="CanonicalRequestedModel"/> + <see cref="LaunchModel"/> are populated
/// and <see cref="EquivalenceKey"/> is the stable anchor. On <c>"unavailable"</c>,
/// <see cref="RecognizedVendor"/> optionally names a DIFFERENT advertised unattended vendor on this
/// same daemon that recognized the model (→ the server reports a vendor mismatch). On <c>"invalid"</c>,
/// <see cref="DiagnosticCode"/> optionally carries a bounded reason token.</summary>
public sealed record ReviewerModelResolveResponseV1(
    string  RequestId,
    string  Vendor,
    string  PolicyVersion,
    string  Disposition,
    string? CanonicalRequestedModel = null,
    string? LaunchModel             = null,
    string? EquivalenceKey          = null,
    string? RecognizedVendor        = null,
    string? DiagnosticCode          = null);

/// <summary>Daemon → server (hub method <c>ReportExplicitReviewerModelResolved</c>): the CONCRETE model
/// an explicit-model reviewer actually launched with, keyed by the preallocated <see cref="AgentId"/> +
/// the durable <see cref="LaunchAttemptId"/>. <see cref="Vendor"/>/<see cref="PolicyVersion"/>/
/// <see cref="EquivalenceKey"/> echo the values the preflight accepted so the server validates by
/// equality; <see cref="ResolvedModel"/> is the exact concrete model id the server re-prices before
/// recording the assignment.</summary>
public sealed record ExplicitReviewerModelResolvedV1(
    string AgentId,
    string LaunchAttemptId,
    string Vendor,
    string ResolvedModel,
    string PolicyVersion,
    string EquivalenceKey);

/// <summary>
/// Discriminator for daemon launch commands. <see cref="Default"/> preserves
/// the existing prompt-driven launch; <see cref="Review"/> uses
/// <see cref="ReviewLaunchInfo"/> + <c>BaseRef</c> to drive a hosted PR review;
/// <see cref="ReviewFlow"/> marks a durable agent-review-flow reviewer, which the
/// daemon runs unattended (never approval + no MCP). The value crosses the CLI↔server wire, so
/// it MUST stay Default=0, Review=1, ReviewFlow=2.
/// </summary>
public enum LaunchKind {
    Default    = 0,
    Review     = 1,
    ReviewFlow = 2
}

// ── Phase B (D2): daemon self-report DTOs ────────────────────────────────────────────────

/// <summary>Phase B (D2): one live hosted agent in the daemon's self-report. <see cref="Kind"/>
/// is the <see cref="LaunchKind"/> name; <see cref="FlowRunId"/>/<see cref="FlowRole"/> are set only
/// for a ReviewFlow launch. Carried additively on <see cref="DaemonConnect.LiveAgents"/> and in
/// <see cref="DaemonStatusReport"/> so the server can associate a surviving unassigned reviewer with
/// its role instead of a blind grace period. All-optional trailing fields keep it wire-compatible.</summary>
public readonly record struct LiveAgentInfo(
        string         Id,
        string         Kind,
        DateTimeOffset CreatedAt,
        string?        FlowRunId = null,
        string?        FlowRole  = null
    );

/// <summary>Phase B (D4 §6.4(2a)): an agent whose death could NOT be confirmed (record-write
/// or kill failure) and is being retried by the daemon heartbeat. Same shape as
/// <see cref="LiveAgentInfo"/>; reported separately so the server can see it counts against admission
/// (<c>EffectiveCount = ActiveCount + Quarantined.Count</c>) without changing <c>ActiveCount</c>'s
/// meaning.</summary>
public readonly record struct QuarantinedAgentInfo(
        string         Id,
        string         Kind,
        DateTimeOffset CreatedAt,
        string?        FlowRunId = null,
        string?        FlowRole  = null
    );

/// <summary>Phase B (D2): the periodic (60s) one-way daemon→server self-report. Sent via a
/// one-way <c>SendAsync</c> (never <c>InvokeAsync</c>) so an old server without the handler produces
/// only a server-side log line, not a client fault. <see cref="ActiveCount"/> is exactly the daemon's
/// Starting/Running agent count (its wire meaning never changes).</summary>
public readonly record struct DaemonStatusReport(
        int                  ActiveCount,
        LiveAgentInfo[]      LiveAgents,
        QuarantinedAgentInfo[] Quarantined,
        // Phase B2-b (sequenced-settlement design): additive heal-barrier / startup-completeness
        // fields. All trailing/optional — an old server ignores them, an old daemon never sets them.
        // Epoch is the shipped per-boot _daemonEpoch (reused, never a second epoch concept).
        string?                       Epoch                         = null,
        long?                         LastProcessedSeq              = null,
        long?                         HighestAcceptedSeq            = null,
        bool?                         StartupReapComplete           = null,
        ResolvedStartupCandidate[]?   ResolvedStartupCandidates     = null,
        UnresolvedStartupCandidate[]? UnresolvedStartupCandidates   = null,
        StartupDiscovery?             StartupDiscovery              = null,
        // Phase B2-b (sequenced-settlement design §5.5): the daemon-lifetime monotonic high-water of the
        // resolved-candidates ledger, advertised alongside ResolvedStartupCandidates so that once sparse
        // acks prune entries the server still knows the generation frontier. Additive/optional.
        long?                         HighestResolutionGeneration   = null
    );

// ── Phase B2-b (sequenced-settlement design): startup-completeness / heal-barrier report DTOs ──

/// <summary>Phase B2-b (sequenced-settlement design): positive per-id death evidence for a
/// prior-incarnation startup candidate. <see cref="Generation"/> is a daemon-lifetime monotonic
/// ack/ordering id; <c>(AgentId, OldEpoch)</c> is the crash-reconciliation + server-upsert identity.
/// Flow fields come ONLY from a trusted record-tracked resolved entry — never from a recordless
/// marker kill (mutable env).</summary>
public readonly record struct ResolvedStartupCandidate(
        long    Generation,
        string  AgentId,
        string  OldEpoch,
        string? FlowRunId = null,
        string? FlowRole  = null
    );

/// <summary>Phase B2-b (sequenced-settlement design): why a known-id prior-incarnation candidate is
/// still blocked (keeps <c>StartupReapComplete</c> false). The zero value <see cref="PendingMarker"/>
/// is the conservative default.</summary>
public enum StartupCandidateUnresolvedReason {
    [JsonStringEnumMemberName("pending_marker")]        PendingMarker        = 0,
    [JsonStringEnumMemberName("legacy_unresolvable")]   LegacyUnresolvable   = 1,
    [JsonStringEnumMemberName("identity_unresolvable")] IdentityUnresolvable = 2,
}

/// <summary>Phase B2-b (sequenced-settlement design): a known-id prior-incarnation candidate that is
/// blocked (keeps <c>StartupReapComplete</c> false).</summary>
public readonly record struct UnresolvedStartupCandidate(
        string                           AgentId,
        StartupCandidateUnresolvedReason Reason,
        string?                          FlowRunId = null,
        string?                          FlowRole  = null
    );

/// <summary>Phase B2-b (sequenced-settlement design): recordless-survivor marker-scan status. The
/// zero value <see cref="Pending"/> is the conservative default — a missing field or an intermediate
/// daemon reads as <see cref="Pending"/> (never <see cref="Complete"/>).</summary>
public enum MarkerScanState {
    [JsonStringEnumMemberName("pending")]        Pending       = 0, // conservative default (missing field / intermediate daemon)
    [JsonStringEnumMemberName("complete")]       Complete      = 1,
    [JsonStringEnumMemberName("failed")]         Failed        = 2,
    [JsonStringEnumMemberName("not_applicable")] NotApplicable = 3, // Windows (no scan) / macOS (env redacted)
}

/// <summary>Phase B2-b (sequenced-settlement design): recordless-survivor discovery status; lets the
/// server render WHY <c>StartupReapComplete</c> is false.</summary>
public readonly record struct StartupDiscovery(
        MarkerScanState MarkerScanState,
        DateTimeOffset? LastSuccessfulScanAt = null
    );

// ── Phase B2-b (sequenced-settlement design): command / ack side wire DTOs ──

/// <summary>Phase B2-b (sequenced-settlement design): the sequenced stop primitive. A capability
/// daemon receives this instead of the legacy <c>StopAgent</c>; <see cref="Epoch"/> is the shipped
/// per-boot <c>_daemonEpoch</c>, <see cref="Seq"/> the lane sequence number, <see cref="CommandId"/>
/// a GUID string.</summary>
public readonly record struct StopAgentV2(
        string AgentId,
        string Epoch,
        long   Seq,
        string CommandId
    );

/// <summary>Phase B2-b (sequenced-settlement design): ack lifecycle state. The zero value
/// <see cref="Accepted"/> is the conservative default (accepted, not yet terminally processed);
/// <see cref="Processed"/> is terminal and carries <c>OutcomeKind</c> + <c>CurrentState</c>.</summary>
public enum CommandAckState {
    [JsonStringEnumMemberName("accepted")]  Accepted  = 0, // accepted, not yet terminally processed
    [JsonStringEnumMemberName("processed")] Processed = 1, // terminal; OutcomeKind + CurrentState set
}

/// <summary>Phase B2-b (sequenced-settlement design): terminal outcome of a processed sequenced
/// command. The zero value <see cref="LaunchExecuted"/> is the conservative default.</summary>
public enum CommandOutcomeKind {
    [JsonStringEnumMemberName("launch_executed")]       LaunchExecuted      = 0,
    [JsonStringEnumMemberName("launch_rejected")]       LaunchRejected      = 1,
    [JsonStringEnumMemberName("launch_failed_cleaned")] LaunchFailedCleaned = 2,
    [JsonStringEnumMemberName("stop_executed")]         StopExecuted        = 3,
    [JsonStringEnumMemberName("internal_error")]        InternalError       = 4,
}

/// <summary>Phase B2-b (sequenced-settlement design): current liveness read live at ack time
/// (confirmed-death precedence Live &gt; Quarantined &gt; Dead). The zero value <see cref="Live"/> is
/// the conservative default. <see cref="NotFound"/> is a defined wire value the daemon's liveness read
/// collapses to <see cref="Dead"/>, but a future path may still emit distinctly.</summary>
public enum AgentLiveness {
    [JsonStringEnumMemberName("live")]        Live        = 0,
    [JsonStringEnumMemberName("quarantined")] Quarantined = 1,
    [JsonStringEnumMemberName("dead")]        Dead        = 2,
    [JsonStringEnumMemberName("not_found")]   NotFound    = 3,
}

/// <summary>Phase B2-b (sequenced-settlement design): the daemon's answer to a sequenced command
/// (including an exact-duplicate — NO re-execution). <c>OutcomeKind</c>/<c>CurrentState</c> are set
/// iff <see cref="State"/> == <see cref="CommandAckState.Processed"/>.</summary>
public readonly record struct CommandAck(
        string              Epoch,
        long                Seq,
        string              CommandId,
        CommandAckState     State,
        CommandOutcomeKind? OutcomeKind     = null, // set iff State == Processed
        AgentLiveness?      CurrentState    = null, // set iff State == Processed
        string?             AgentId         = null,
        string?             SessionId       = null,
        string?             RejectionReason = null
    );

/// <summary>Phase B2-b (sequenced-settlement design): why a sequenced command was terminally
/// rejected (never advances the old epoch's watermark). The zero value <see cref="WrongNext"/> is
/// the conservative default.</summary>
public enum CommandRejectedReason {
    [JsonStringEnumMemberName("wrong_next")]           WrongNext          = 0,
    [JsonStringEnumMemberName("duplicate_collision")]  DuplicateCollision = 1,
    [JsonStringEnumMemberName("stale_epoch")]          StaleEpoch         = 2,
    [JsonStringEnumMemberName("daemon_capacity")]      DaemonCapacity     = 3,
    [JsonStringEnumMemberName("backpressure")]         Backpressure       = 4,
    [JsonStringEnumMemberName("internal_error")]       InternalError      = 5,
    [JsonStringEnumMemberName("semantic")]             Semantic           = 6,
}

/// <summary>Phase B2-b (sequenced-settlement design): terminal rejection of a sequenced command
/// (never advances the old epoch's watermark).</summary>
public readonly record struct CommandRejected(
        string                Epoch,
        long                  Seq,
        string                CommandId,
        CommandRejectedReason Reason,
        string?               AgentId = null
    );

/// <summary>Phase B2-b (sequenced-settlement design): server→daemon retirement proof — the daemon
/// may retire identity-cache entries &lt;= <see cref="UpToSeq"/> for <see cref="Epoch"/>.</summary>
public readonly record struct AckProcessedPrefix(
        string Epoch,
        long   UpToSeq
    );

/// <summary>Phase B2-b (sequenced-settlement design): one resolved-candidate ack entry (sparse,
/// per-entry prune — no head-of-line retention).</summary>
public readonly record struct ResolvedCandidateAck(
        long   Generation,
        string AgentId,
        string OldEpoch
    );

/// <summary>Phase B2-b (sequenced-settlement design): server→daemon prune of individual
/// resolved-candidate ledger entries.</summary>
public readonly record struct AckResolvedCandidates(
        ResolvedCandidateAck[] Entries
    );

/// <summary>M1-A (spec §4.3): distinguishes a record with a comparable start-identity
/// (<see cref="Present"/>) from one where native capture failed (<see cref="IdentityUnavailable"/>
/// — <see cref="AgentPidRecord.StartIdentity"/> is <c>""</c>, a deliberate well-formed marker,
/// not a launch failure). <see cref="Present"/> MUST be the zero value: a pre-M1-A record's
/// JSON has no <c>identity_kind</c> key at all, and System.Text.Json's constructor-based
/// deserialization gives a missing value-type constructor parameter <c>default(T)</c> — this
/// is precisely how the backward-compat rule ("missing identity_kind + nonempty start_identity
/// ⇒ present") is satisfied with NO custom converter.</summary>
public enum PidIdentityKind {
    Present             = 0,
    IdentityUnavailable = 1,
}

/// <summary>Phase B (D4 §6.4(2)): the durable per-agent PID record written atomically at spawn
/// to <c>&lt;state-dir&gt;/agents/{agentId}.json</c>, so a restarted daemon can reap a surviving child
/// by EXACT identity. <see cref="StartIdentity"/> is the <c>ProcessStartToken</c> string
/// (kernel starttime / absolute start ticks / macOS incarnation id — exact, no tolerance), or
/// <c>""</c> when <see cref="IdentityKind"/> is <see cref="PidIdentityKind.IdentityUnavailable"/>.
/// <see cref="DaemonId"/> = hash of the daemon state-dir path (stable logical identity);
/// <see cref="DaemonEpoch"/> = fresh per boot.</summary>
public readonly record struct AgentPidRecord(
        string          AgentId,
        int             Pid,
        string          StartIdentity,
        PidIdentityKind IdentityKind,
        string          Kind,
        string          Vendor,
        string?         FlowRunId,
        string?         FlowRole,
        string          DaemonId,
        string          DaemonEpoch,
        DateTimeOffset  SpawnedAt
    );

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

/// <summary>
/// Daemon reply to the server's <c>ProbeBorrowSource</c> client-result invocation (Phase A,
/// task A3): "can you borrow this path?". <see cref="CanBorrow"/> mirrors
/// <c>BorrowAuthResult.Allowed</c>; <see cref="CanonicalCwd"/>/<see cref="CanonicalGitRoot"/> are the
/// daemon-computed canonical paths (non-null only when the path exists), and <see cref="Reason"/>
/// carries the rejection reason (<c>path_absent</c> / <c>not_allowed</c>) when not borrowable. Wire
/// keys (snake_case): <c>can_borrow</c>, <c>canonical_cwd</c>, <c>canonical_git_root</c>, <c>reason</c>.
/// </summary>
public record BorrowProbeResult(
        bool    CanBorrow,
        string? CanonicalCwd,
        string? CanonicalGitRoot,
        string? Reason
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

/// <summary>
/// Commands sent from daemon clients to the server via SignalR.
///
/// <para><c>InstanceId</c> is a fresh GUID generated at daemon process startup
/// and held only in memory (also written to the daemon's per-name flock
/// file content for diagnostics). The server uses it to distinguish a
/// legitimate reconnect of the same daemon (new SignalR connectionId, same
/// instance) from a different daemon process claiming the same
/// <c>(owner, name)</c> slot. Legacy daemons sent no <c>InstanceId</c>;
/// the server still accepts them under a legacy-displacement fallback.</para>
///
/// <para><c>Version</c> is the daemon binary's
/// <c>AssemblyInformationalVersion</c>. Logged on connect and surfaced on
/// the server's <c>DaemonInfo</c> so the dashboard can show what version
/// each connected daemon is running.</para>
///
/// <para><c>MachineId</c> is this machine's stable id (see
/// <see cref="MachineId"/>), reported so the server can later prove a daemon
/// claiming a given repo path is actually running on the requester's
/// machine. Trailing/optional so an older daemon that doesn't send it (or a
/// newer daemon talking to an older server that ignores it) never breaks.</para>
/// </summary>
public readonly record struct DaemonConnect(
        string    Name,
        string    Platform,
        string[]  RepoPaths,
        int       MaxAgents,
        string[]  LiveAgentIds,
        string?   InstanceId       = null,
        string?   Version          = null,
        string[]? SupportedVendors = null,
        string?   MachineId        = null,
        // Phase B (D2): richer live-agent metadata alongside the existing LiveAgentIds
        // (kept for back-compat). Trailing/optional — old servers ignore it, old daemons never set it.
        LiveAgentInfo[]? LiveAgents = null,
        // Reviewer vendor override support: vendor tokens this daemon can run fully UNATTENDED (a
        // subset of SupportedVendors — every entry here MUST also appear there). Null from a daemon
        // build that predates this field — that daemon is simply not an override-eligible target for
        // ANY vendor. There is deliberately no fallback that widens a null to anything non-null.
        string[]? UnattendedVendors = null,
        // Phase B2-b (sequenced-settlement design): additive startup-completeness / heal-barrier
        // capability payload. All trailing/optional — old servers ignore it, old daemons never set
        // it. Advertised-but-inert until the paired server PR consumes it (gated on
        // SupportsSequencedCommands). Epoch is the shipped per-boot _daemonEpoch (reused).
        QuarantinedAgentInfo[]?       Quarantined                   = null,
        string?                       Epoch                         = null,
        long?                         HighestAcceptedSeq            = null,
        long?                         LastProcessedSeq              = null,
        bool?                         StartupReapComplete           = null,
        ResolvedStartupCandidate[]?   ResolvedStartupCandidates     = null,
        UnresolvedStartupCandidate[]? UnresolvedStartupCandidates   = null,
        StartupDiscovery?             StartupDiscovery              = null,
        bool?                         RecordlessSurvivorsImpossible = null, // absent/false ⇒ has a recordless class
        bool                          SupportsSequencedCommands     = false, // THE capability gate
        // Phase B2-b (sequenced-settlement design §5.5): the daemon-lifetime monotonic high-water of the
        // resolved-candidates ledger, advertised alongside ResolvedStartupCandidates so that once sparse
        // acks prune entries the server still knows the generation frontier. Additive/optional.
        long?                         HighestResolutionGeneration   = null,
        // Structured per-vendor unattended-review certification facts. The legacy string
        // list above remains the compatibility surface; this trailing field adds versioned proof.
        IReadOnlyList<UnattendedVendorCapability>? UnattendedVendorCapabilities = null
    );

public sealed record UnattendedVendorCapability(
    string Vendor,
    string? CliVersion,
    string LauncherPolicyVersion,
    bool BorrowedReviewSupported,
    string? BorrowedReviewContainment = null,
    // True only when this vendor is installed, unattended-certified, and has a runtime resolver; the
    // server refuses a v3 model override unless true. Defaults false — a legacy/mid-rollout daemon is
    // never widened to "supported" by any fallback.
    bool SupportsReviewerModelResolution = false,
    // This vendor's reviewer-model policy version (distinct from LauncherPolicyVersion); the server
    // echoes it to detect a mid-flight policy change. Null when no resolver is advertised.
    string? ReviewerModelPolicyVersion = null,
    // Whether this daemon accepts a caller-selected launch posture for this vendor
    // (CodexLaunchPosture on LaunchAgentCommand). Defaults false — a legacy or mid-rollout daemon is
    // never widened to "supported" by any fallback, so the server refuses posture selection rather
    // than sending a block that would be silently ignored.
    bool SupportsLaunchPosture = false
);

public readonly record struct AgentRegistered(
        string  AgentId,
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        // Applied Codex posture echo: the pair actually passed to --sandbox / --ask-for-approval,
        // non-null ONLY for a hosted interactive Codex launch (Kind == Default, owned worktree),
        // whether that pair was caller-selected or derived. Review-flow / PR-review / local-attach
        // agents and non-codex vendors always report null, so a consumer can render it without any
        // launch-kind discriminator. Trailing name-bound fields on a single JSON DTO — an older
        // server ignores them, an older daemon never sets them.
        string? SandboxPolicy  = null,
        string? ApprovalPolicy = null
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
public readonly record struct PrepareEvalCommand(
        string                         EvalRunId,
        string                         SessionId,
        string                         Model,
        bool                           Chain,
        int?                           ThresholdBytes,
        IReadOnlyList<EvalQuestionDto> Questions
    );

/// <summary>Server → daemon: run a single judge question against the cached context.</summary>
public readonly record struct RunQuestionCommand(
        string          EvalRunId,
        EvalQuestionDto Question,
        int             Index,
        int             Total
    );

/// <summary>Server → daemon: aggregate verdicts, run retrospective, persist final result.</summary>
public readonly record struct FinalizeEvalCommand(
        string                             EvalRunId,
        IReadOnlyList<EvalQuestionVerdict> Verdicts,
        string                             Model
    );

/// <summary>Server → daemon: discard any cached context for this run (e.g. dashboard aborted).</summary>
public readonly record struct CancelEvalCommand(string EvalRunId);

/// <summary>Daemon → server: prepare-phase result.</summary>
public readonly record struct PrepareResult(
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
public readonly record struct QuestionResult(
        bool                 Success,
        EvalQuestionVerdict? Verdict,
        string?              Error,
        long                 InputTokens,
        long                 OutputTokens
    );

/// <summary>Daemon → server: finalize-phase result including the aggregate to persist.</summary>
public readonly record struct FinalizeResult(
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
public readonly record struct EvalFailed(string EvalRunId, string SessionId, string Reason);

/// <summary>Daemon → server: retrospective pass is about to start (all category judges have completed).</summary>
public readonly record struct EvalRetrospectiveStarted(string SessionId, string EvalRunId);

/// <summary>Daemon → server: retrospective pass produced a summary and has been folded into the aggregate.</summary>
public readonly record struct EvalRetrospectiveCompleted(string SessionId, string EvalRunId);

/// <summary>Daemon → server: retrospective pass failed; the aggregate is still persisted without a retrospective.</summary>
public readonly record struct EvalRetrospectiveFailed(string SessionId, string EvalRunId, string Reason);

/// <summary>Agent run events posted to the server HTTP API.</summary>
public record AgentRunStarted(
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        string? WorktreePath,
        string  Vendor
    );

public record AgentRunStopped(string? Reason, int? ExitCode);

public record AgentRunHeartbeat(string? SessionId);

/// <summary>
/// Returned by the server's <c>EndAgentSession</c> SignalR hub method. Mirrors the
/// server-side record of the same name. SessionId is surfaced because the daemon
/// only knows agentId — it can't spawn <c>kcap generate-whats-done</c> without
/// the sessionId, which the server resolves via FindAgentSessionIdAsync.
/// </summary>
public record EndAgentSessionResult {
    [JsonPropertyName("generate_whats_done")]
    public bool GenerateWhatsDone { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}
