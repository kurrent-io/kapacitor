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
    DateTimeOffset Timestamp);

record RecapEntry(
    string         Type,
    string?        SessionId,
    string?        AgentId,
    string?        AgentType,
    string         Content,
    string?        FilePath,
    DateTimeOffset Timestamp);

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
    public RepositoryPayload? LastSentRepository  { get; set; }
    public DateTimeOffset     LastRepoDetection  { get; set; }
    public bool               TitleGenerated     { get; set; }
    public int                TitleAttempts       { get; set; }
    public bool               TitleInFlight       { get; set; }
    public string?            FirstUserText       { get; set; }
    public bool               IsSlashCommand      { get; set; }
    public string?            SlashCommandName    { get; set; }
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

enum HistorySessionStatus { New, Partial, AlreadyLoaded }

class SessionMetadata {
    public string?         Cwd            { get; set; }
    public string?         Model          { get; set; }
    public string?         Slug           { get; set; }
    public string?         SessionId      { get; set; }
    public DateTimeOffset? FirstTimestamp { get; set; }
}

static partial class GitUrlParser {
    public static (string? Owner, string? RepoName) ParseRemoteUrl(string? url) {
        if (url is null) return (null, null);

        var sshMatch = SshRegex().Match(url);
        if (sshMatch.Success)
            return (sshMatch.Groups["owner"].Value, sshMatch.Groups["repo"].Value);

        var httpsMatch = HttpsRegex().Match(url);
        return httpsMatch.Success
            ? (httpsMatch.Groups["owner"].Value, httpsMatch.Groups["repo"].Value)
            : (null, null);
    }

    [GeneratedRegex(@"https?://[^/]+/(?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    [GeneratedRegex(@"git@[\w.-]+:(?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$")]
    internal static partial Regex SshRegex();
}

[JsonSerializable(typeof(List<RecapEntry>))]
[JsonSerializable(typeof(List<ErrorEntry>))]
[JsonSerializable(typeof(RepositoryPayload))]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSerializable(typeof(TranscriptBatch))]
[JsonSerializable(typeof(SessionTitlePayload))]
[JsonSerializable(typeof(WhatsDonePayload))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
partial class KapacitorJsonContext : JsonSerializerContext;