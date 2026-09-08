using System.Globalization;
using System.Text.Json;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>Reads <c>gh</c> JSON into the wire records. Every entry point takes the raw text and tolerates any shape; null means malformed.</summary>
public static class GitHubCliMapping {
    static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    public static JsonDocument? Parse(string json) {
        try { return JsonDocument.Parse(json, Options); }
        catch (JsonException) { return null; }
    }

    public static HashSet<string>? SignedInHosts(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("hosts") is not { } hosts || !hosts.IsObject) return null;
        var signedIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts.EnumerateObject()) {
            if (!host.Value.IsArray || !GitHubCliRunner.ValidHost(host.Name)) continue;
            if (host.Value.EnumerateArray().Any(entry => entry.IsObject && entry.Prop("state") is { } state && state.IsString && state.GetString() == "success"))
                signedIn.Add(host.Name);
        }
        return signedIn;
    }

    public static IReadOnlyList<PullRequestLinkDto> Links(string json, PullRequestRepository repository) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsArray) return [];
        var links = new List<PullRequestLinkDto>();
        foreach (var row in document.RootElement.EnumerateArray()) {
            if (!row.IsObject || row.Prop("number") is not { } number || !number.IsNumber || !number.TryGetInt32(out var value) || value <= 0) continue;
            links.Add(new() { Provider = "github", Host = repository.Host, RepoHash = repository.RepoHash, Owner = repository.Owner, RepoName = repository.RepoName,
                Number = value, Url = PullRequestWire.SafeLink(Text(row, "url")), Title = Text(row, "title"), HeadRef = Text(row, "headRefName") });
            if (links.Count == 20) break;
        }
        return links;
    }

    public static string? Text(JsonElement element, string name) => element.Prop(name) is { } value && value.IsString ? value.GetString() : null;
    public static DateTime? Time(JsonElement element, string name) => element.Prop(name) is { } value && value.IsString
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at.UtcDateTime : null;

    public const int BodyLimit = 262_144;
    public const int ToolListLimit = 100;
    public const string ViewFields = "title,url,state,isDraft,headRefName,baseRefName,headRefOid,body,updatedAt,reviewDecision,author,statusCheckRollup,reviewRequests,latestReviews,reviews,comments";

    public static (string? Text, bool Truncated) Truncate(string? text) => text is { Length: > BodyLimit } ? (text[..BodyLimit], true) : (text, false);

    public static GitHubCliView? View(string json, PullRequestSubjectDto subject, DateTime fetchedAt) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject) return null;
        var root = document.RootElement;
        var headSha = Text(root, "headRefOid");
        var checks = Checks(root.Prop("statusCheckRollup"), headSha);
        var reviewers = Reviewers(root.Prop("reviewRequests"), root.Prop("latestReviews"));
        var reviews = Reviews(root.Prop("reviews"), out var reviewsCapped);
        var comments = Comments(root.Prop("comments"), out var commentsCapped);
        var (description, truncated) = Truncate(Text(root, "body"));
        var availability = new PullRequestAvailabilityDto { Status = "ready", FetchedAt = fetchedAt };
        var counts = checks.GroupBy(check => check.Outcome ?? "unknown", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new PullRequestCountDto { Kind = "exact", Value = group.Count() }, StringComparer.Ordinal);
        var latest = root.Prop("latestReviews") is { } latestReviews && latestReviews.IsArray ? latestReviews.EnumerateArray().ToArray() : [];
        var requests = root.Prop("reviewRequests") is { } reviewRequests && reviewRequests.IsArray ? reviewRequests.EnumerateArray().ToArray() : [];
        var overview = new PullRequestOverviewDto {
            Title = Text(root, "title"), Url = PullRequestWire.SafeLink(Text(root, "url")), Lifecycle = Lifecycle(Text(root, "state"), root.Bool("isDraft")),
            IsDraft = root.Bool("isDraft"), HeadRef = Text(root, "headRefName"), BaseRef = Text(root, "baseRefName"),
            HeadSha = headSha, Description = description, DescriptionTruncated = truncated, UpdatedAt = Time(root, "updatedAt"),
            ReviewDecision = ReviewDecision(Text(root, "reviewDecision")), AccessCheckedFor = "your GitHub CLI sign-in",
            Checks = new() { Availability = availability, Rollup = Rollup(checks), HeadSha = headSha, Counts = counts },
            Reviews = new() { Availability = availability, Published = Count(reviews.Length, reviewsCapped),
                Approved = Exact(latest.Count(review => Text(review, "state") == "APPROVED")),
                ChangesRequested = Exact(latest.Count(review => Text(review, "state") == "CHANGES_REQUESTED")),
                OutstandingUsers = Exact(requests.Count(request => Text(request, "__typename") == "User")),
                OutstandingTeams = Exact(requests.Count(request => Text(request, "__typename") == "Team")) },
            Conversation = new() { Availability = availability, Count = Count(comments.Length, commentsCapped) },
        };
        return new(overview, headSha, fetchedAt, checks, reviewers, reviews, reviewsCapped, comments, commentsCapped);
    }

    static PullRequestCountDto Exact(int value) => new() { Kind = "exact", Value = value };
    static PullRequestCountDto Count(int value, bool capped) => new() { Kind = capped ? "lower_bound" : "exact", Value = value };

    static string Lifecycle(string? state, bool? draft) => state switch {
        "MERGED" => "merged", "CLOSED" => "closed", "OPEN" => draft == true ? "draft" : "open", _ => "unknown"
    };
    static string? ReviewDecision(string? value) => value switch {
        "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes_requested", "REVIEW_REQUIRED" => "review_required", _ => null };
    static string? ReviewState(string? value) => value switch {
        "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes_requested", "COMMENTED" => "commented", "DISMISSED" => "dismissed", "PENDING" => "pending", _ => null };
    static string? Rollup(PullRequestCheckDto[] checks) => checks.Length == 0 ? null
        : checks.Any(check => check.Outcome is "failure" or "timed_out" or "action_required") ? "failure"
        : checks.Any(check => check.Outcome == "pending") ? "pending" : "success";

    static PullRequestCheckDto[] Checks(JsonElement? rollup, string? headSha) {
        if (rollup is not { } array || !array.IsArray) return [];
        var checks = new List<PullRequestCheckDto>();
        foreach (var entry in array.EnumerateArray()) {
            if (!entry.IsObject) continue;
            var index = checks.Count.ToString(CultureInfo.InvariantCulture);
            if (Text(entry, "__typename") == "CheckRun") {
                var status = Text(entry, "status"); var conclusion = Text(entry, "conclusion");
                checks.Add(new() { Id = "check-" + index, Availability = "available", Url = PullRequestWire.CheckLink(Text(entry, "detailsUrl")), Name = Text(entry, "name"),
                    AppName = Text(entry, "workflowName"), Source = "check_run", Outcome = CheckOutcome(status, conclusion), Status = status?.ToLowerInvariant(),
                    Conclusion = string.IsNullOrEmpty(conclusion) ? null : conclusion.ToLowerInvariant(), StartedAt = Time(entry, "startedAt"), CompletedAt = Time(entry, "completedAt"), HeadSha = headSha });
            } else if (Text(entry, "__typename") == "StatusContext") {
                var state = Text(entry, "state");
                checks.Add(new() { Id = "status-" + index, Availability = "available", Url = PullRequestWire.CheckLink(Text(entry, "targetUrl")), Name = Text(entry, "context"),
                    Source = "status", Outcome = StatusOutcome(state), Status = state?.ToLowerInvariant(), StartedAt = Time(entry, "startedAt"), HeadSha = headSha });
            }
        }
        return [.. checks];
    }
    static string CheckOutcome(string? status, string? conclusion) => status != "COMPLETED" ? "pending" : conclusion switch {
        "SUCCESS" => "success", "FAILURE" or "STARTUP_FAILURE" => "failure", "NEUTRAL" => "neutral", "SKIPPED" => "skipped", "CANCELLED" => "cancelled",
        "TIMED_OUT" => "timed_out", "ACTION_REQUIRED" => "action_required", "STALE" => "stale", _ => "unknown" };
    static string StatusOutcome(string? state) => state switch { "SUCCESS" => "success", "FAILURE" or "ERROR" => "failure", "PENDING" or "EXPECTED" => "pending", _ => "unknown" };

    static PullRequestReviewerDto[] Reviewers(JsonElement? requests, JsonElement? latest) {
        var reviewers = new List<PullRequestReviewerDto>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (requests is { } requested && requested.IsArray)
            foreach (var request in requested.EnumerateArray()) {
                if (!request.IsObject) continue;
                var team = Text(request, "__typename") == "Team";
                var id = team ? Text(request, "slug") ?? Text(request, "name") : Text(request, "login");
                if (id is null) continue;
                index[id] = reviewers.Count;
                reviewers.Add(new() { Id = "reviewer:" + id, Availability = "available", Requested = true,
                    Actor = new() { Id = id, Kind = team ? "team" : "user", Login = team ? null : id, Name = team ? Text(request, "name") ?? id : null } });
            }
        if (latest is { } reviews && reviews.IsArray)
            foreach (var review in reviews.EnumerateArray()) {
                if (!review.IsObject || review.Prop("author") is not { } author || Text(author, "login") is not { } login) continue;
                var mapped = new PullRequestReviewerDto { Id = "reviewer:" + login, Availability = "available", Requested = false,
                    Actor = new() { Id = login, Kind = "user", Login = login }, ReviewState = ReviewState(Text(review, "state")), SubmittedAt = Time(review, "submittedAt") };
                if (index.TryGetValue(login, out var at)) reviewers[at] = mapped with { Requested = true };
                else { index[login] = reviewers.Count; reviewers.Add(mapped); }
            }
        return [.. reviewers];
    }

    static PullRequestReviewDto[] Reviews(JsonElement? element, out bool capped) {
        capped = false;
        if (element is not { } array || !array.IsArray) return [];
        capped = array.GetArrayLength() >= ToolListLimit;
        var reviews = new List<PullRequestReviewDto>();
        foreach (var review in array.EnumerateArray()) {
            if (!review.IsObject || Text(review, "state") == "PENDING") continue;
            var (body, truncated) = Truncate(Text(review, "body"));
            var login = review.Prop("author") is { } author ? Text(author, "login") : null;
            var id = Text(review, "id") is { Length: > 0 } nodeId ? nodeId : "review-" + reviews.Count.ToString(CultureInfo.InvariantCulture);
            reviews.Add(new() { Id = id, Availability = "available", Author = login is null ? null : new() { Id = login, Kind = "user", Login = login },
                Body = body, BodyTruncated = truncated, State = ReviewState(Text(review, "state")), SubmittedAt = Time(review, "submittedAt") });
        }
        return [.. reviews];
    }

    static PullRequestCommentDto[] Comments(JsonElement? element, out bool capped) {
        capped = false;
        if (element is not { } array || !array.IsArray) return [];
        capped = array.GetArrayLength() >= ToolListLimit;
        var comments = new List<PullRequestCommentDto>();
        foreach (var comment in array.EnumerateArray()) {
            if (!comment.IsObject || Text(comment, "id") is not { Length: > 0 } id) continue;
            comments.Add(Comment(comment, id, null));
        }
        return [.. comments];
    }

    public static PullRequestCommentDto Comment(JsonElement comment, string id, string? replyTo) {
        var (body, truncated) = Truncate(Text(comment, "body"));
        var login = comment.Prop("author") is { } author ? Text(author, "login") : null;
        return new() { Id = id, Availability = "available", Url = PullRequestWire.SafeLink(Text(comment, "url")), Author = login is null ? null : new() { Id = login, Kind = "user", Login = login },
            Body = body, BodyTruncated = truncated, CreatedAt = Time(comment, "createdAt"), UpdatedAt = Time(comment, "updatedAt"), PublishedAt = Time(comment, "publishedAt"), ReplyToId = replyTo };
    }

    public const string ThreadsQuery = "query($owner:String!,$repo:String!,$number:Int!,$after:String){repository(owner:$owner,name:$repo){pullRequest(number:$number){headRefOid reviewThreads(first:50,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id isResolved isOutdated path line startLine originalLine originalStartLine diffSide startDiffSide subjectType comments(first:1){totalCount nodes{id url body createdAt updatedAt publishedAt diffHunk author{login}}}}}}}}";
    public const string ThreadCommentsQuery = "query($id:ID!,$after:String){node(id:$id){... on PullRequestReviewThread{comments(first:50,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id url body createdAt updatedAt publishedAt replyTo{id} author{login}}}}}}";

    public static GitHubCliThreadsPage? Threads(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("data") is not { } data || !data.IsObject) return null;
        var pull = data.Prop("repository") is { } repository && repository.IsObject ? repository.Prop("pullRequest") : null;
        if (pull is null || pull.Value.IsNull) return new(false, null, 0, false, null, []);
        if (!pull.Value.IsObject || pull.Value.Prop("reviewThreads") is not { } connection || !connection.IsObject
            || connection.Prop("nodes") is not { } nodes || !nodes.IsArray) return null;
        var threads = new List<PullRequestThreadDto>();
        foreach (var node in nodes.EnumerateArray()) {
            if (!node.IsObject || Text(node, "id") is not { Length: > 0 } id || !GitHubCliRunner.ValidNodeId(id)) continue;
            var first = node.Prop("comments") is { } comments && comments.IsObject && comments.Prop("nodes") is { } list && list.IsArray
                ? list.EnumerateArray().FirstOrDefault(comment => comment.IsObject) : default;
            var root = first.IsObject && Text(first, "id") is { Length: > 0 } commentId ? Comment(first, commentId, null) : null;
            var (hunk, hunkTruncated) = Truncate(first.IsObject ? Text(first, "diffHunk") : null);
            threads.Add(new() { Id = id, Availability = "available", Url = root?.Url, IsResolved = node.Bool("isResolved"), IsOutdated = node.Bool("isOutdated"),
                Path = Text(node, "path"), DiffSide = Text(node, "diffSide")?.ToLowerInvariant(), StartDiffSide = Text(node, "startDiffSide")?.ToLowerInvariant(),
                Line = Number(node, "line"), StartLine = Number(node, "startLine"), OriginalLine = Number(node, "originalLine"), OriginalStartLine = Number(node, "originalStartLine"),
                SubjectType = Text(node, "subjectType")?.ToLowerInvariant(), DiffHunk = hunk, HunkTruncated = hunkTruncated, RootComment = root,
                Comments = node.Prop("comments") is { } count && count.IsObject && Number(count, "totalCount") is { } total ? new() { Kind = "exact", Value = total } : null });
        }
        return new(true, Text(pull.Value, "headRefOid"), Number(connection, "totalCount") ?? threads.Count, HasNext(connection), EndCursor(connection), [.. threads]);
    }

    public static GitHubCliCommentsPage? ThreadComments(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("data") is not { } data || !data.IsObject) return null;
        var node = data.Prop("node");
        if (node is null || node.Value.IsNull) return new(false, 0, false, null, []);
        if (!node.Value.IsObject || node.Value.Prop("comments") is not { } connection || !connection.IsObject
            || connection.Prop("nodes") is not { } nodes || !nodes.IsArray) return null;
        var comments = new List<PullRequestCommentDto>();
        foreach (var comment in nodes.EnumerateArray()) {
            if (!comment.IsObject || Text(comment, "id") is not { Length: > 0 } id) continue;
            var replyTo = comment.Prop("replyTo") is { } parent && parent.IsObject ? Text(parent, "id") : null;
            comments.Add(Comment(comment, id, replyTo));
        }
        return new(true, Number(connection, "totalCount") ?? comments.Count, HasNext(connection), EndCursor(connection), [.. comments]);
    }

    static int? Number(JsonElement element, string name) => element.Prop(name) is { } value && value.IsNumber && value.TryGetInt32(out var number) ? number : null;
    static bool HasNext(JsonElement connection) => connection.Prop("pageInfo") is { } info && info.IsObject && info.Bool("hasNextPage") == true;
    static string? EndCursor(JsonElement connection) => connection.Prop("pageInfo") is { } info && info.IsObject && Text(info, "endCursor") is { } cursor
        && GitHubCliRunner.ValidNodeId(cursor) ? cursor : null;

    public static PullRequestRead<T> Failure<T>(GitHubCliResult result, PullRequestSubjectDto subject, DateTime now) where T : class {
        switch (result.Outcome) {
            case GitHubCliOutcome.NotStarted: return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient");
            case GitHubCliOutcome.TimedOut: return new(PullRequestReadKind.TransportFailure, Subject: subject, Reason: "timeout", AccessFailure: "transient");
            case GitHubCliOutcome.Oversized: return new(PullRequestReadKind.InvalidProtocol, Subject: subject, Reason: "oversized", AccessFailure: "invalid");
        }
        var message = result.Stderr;
        if (message.Contains("Could not resolve to a", StringComparison.Ordinal) || message.Contains("HTTP 404", StringComparison.Ordinal))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "not_found", AccessFailure: "invalid");
        if (message.Contains("HTTP 401", StringComparison.Ordinal) || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_signed_out", AccessFailure: "invalid");
        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "rate_limited", RetryAt: now.AddSeconds(60));
        if (message.Contains("HTTP 403", StringComparison.Ordinal))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_denied", AccessFailure: "denied");
        return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient");
    }
}
