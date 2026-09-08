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
}
