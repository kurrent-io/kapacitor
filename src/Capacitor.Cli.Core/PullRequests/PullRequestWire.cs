using System.Globalization;
using System.Text.Json;

namespace Capacitor.Cli.Core.PullRequests;

public static class PullRequestWire {
    public static bool ValidSubject(PullRequestSubjectDto subject) => subject.Provider is { Length: > 0 and <= 100 }
        && subject.Host is { Length: > 0 and <= 256 } && ValidSegment(subject.RepoHash)
        && ValidSegment(subject.Owner) && ValidSegment(subject.RepoName) && subject.Number > 0;
    public static bool ValidSegment(string? value) => value is { Length: > 0 and <= 256 }
        && value is not ("." or "..") && !value.Any(c => char.IsControl(c) || c is '/' or '\\');
    public static bool ValidHandle(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    public static bool IsGitHub(PullRequestSubjectDto subject) => subject.Provider == "github" && subject.Host == "github.com";
    public static PullRequestSubjectDto Subject(PullRequestLinkDto link) => new() { Provider = link.Provider, Host = link.Host,
        RepoHash = link.RepoHash, Owner = link.Owner, RepoName = link.RepoName, Number = link.Number };
    public static int? KnownCount(PullRequestCountDto? count) => count is { Kind: "exact" or "lower_bound", Value: >= 0 } ? count.Value : null;
    public static string? SafeLink(string? value) => value is { Length: > 0 and <= 4096 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort ? uri.AbsoluteUri : null;
    public static string? PrLink(string? value, PullRequestSubjectDto subject) {
        if (SafeLink(value) is not { } safe) return null;
        var uri = new Uri(safe);
        var path = $"/{subject.Owner}/{subject.RepoName}/pull/{subject.Number.ToString(CultureInfo.InvariantCulture)}";
        return uri.Host == "github.com" && (uri.AbsolutePath.TrimEnd('/').Equals(path, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.TrimEnd('/').Equals(path + "/files", StringComparison.OrdinalIgnoreCase)) ? safe : null;
    }
    public static string? CheckLink(string? value) => value is { Length: > 0 and <= 4096 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    public static string? BodyLink(string? value) => value is { Length: > 0 and <= 4096 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

    internal static bool ValidJson(JsonElement root) {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().All(ValidJson);
        if (root.ValueKind != JsonValueKind.Object) return true;
        foreach (var field in root.EnumerateObject()) {
            if (field.Name is "fetched_at" or "retry_at" or "snapshot_started_at" or "snapshot_completed_at" or "created_at" or "updated_at"
                or "submitted_at" or "started_at" or "completed_at" or "published_at") {
                if (field.Value.ValueKind != JsonValueKind.Null && (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString()?.EndsWith('Z') != true
                    || !field.Value.TryGetDateTimeOffset(out var date) || date.Offset != TimeSpan.Zero)) return false;
            }
            if (field.Name == "kind" && root.TryGetProperty("value", out var number)) {
                if (number.ValueKind != JsonValueKind.Null && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var count) || count < 0)) return false;
                if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() == "unknown" && number.ValueKind != JsonValueKind.Null) return false;
            }
            if (field.Name == "counts" && field.Value.ValueKind == JsonValueKind.Object && !field.Value.EnumerateObject().All(x => ValidJson(x.Value))) return false;
            if (field.Name is "data" or "items" or "checks" or "reviews" or "conversation" or "availability" or "root_comment"
                or "published" or "approved" or "changes_requested" or "outstanding_users" or "outstanding_teams" or "count" or "total" or "excluded_by_filter"
                && !ValidJson(field.Value)) return false;
        }
        return true;
    }
    internal static bool ValidData<T>(T value) where T : class => value switch {
        PullRequestLinkListDto links => links.Items is { Length: <= 5000 } && links.Items.All(link => link is not null && ValidSubject(Subject(link))),
        PullRequestOverviewDto => true,
        PullRequestPageDto<PullRequestCheckDto> page => ValidPage(page, row => (row.Id, row.Availability)),
        PullRequestPageDto<PullRequestReviewerDto> page => ValidPage(page, row => (row.Id, row.Availability)),
        PullRequestPageDto<PullRequestReviewDto> page => ValidPage(page, row => (row.Id, row.Availability)),
        PullRequestPageDto<PullRequestThreadDto> page => ValidPage(page, row => (row.Id, row.Availability)),
        PullRequestPageDto<PullRequestCommentDto> page => ValidPage(page, row => (row.Id, row.Availability)),
        _ => false
    };
    static bool ValidPage<T>(PullRequestPageDto<T> page, Func<T, (string Id, string Availability)> identity) where T : class {
        if (!ValidHandle(page.SnapshotId) || !ValidHandle(page.PageCursor) || page.Items is not { Length: <= 50 }
            || page.HasMore != !string.IsNullOrEmpty(page.NextCursor) || page.NextCursor is not null && !ValidHandle(page.NextCursor)
            || page.HasMore && page.Items.Length == 0 || page.SnapshotCompletedAt < page.SnapshotStartedAt
            || page.Total is null || page.ExcludedByFilter is null) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return page.Items.All(row => row is not null && identity(row) is { Id: { Length: > 0 and <= 256 }, Availability: not null } item && ids.Add(item.Id));
    }
}
