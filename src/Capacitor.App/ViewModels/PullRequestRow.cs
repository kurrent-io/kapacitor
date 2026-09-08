namespace Capacitor.App.ViewModels;

public sealed record PullRequestRow(string Id, string Title, string Detail, string? Body, string? Hunk, string? Url,
    string Availability, bool Truncated = false, bool IsThread = false, bool IsCheck = false, string? Outcome = null) {
    public bool HasBody => Availability == "available" && Body is { Length: > 0 };
    public bool HasHunk => Availability == "available" && Hunk is { Length: > 0 };
    public bool HasLink => Url is not null;
    public string ItemNote => Availability switch { "available" => Truncated ? "Preview truncated — full text on GitHub." : "",
        "redacted" => "This item is hidden.", _ => "This item could not be read." };
    internal long Bytes => 256 + 2L * (Id.Length + Title.Length + Detail.Length + (Body?.Length ?? 0) + (Hunk?.Length ?? 0) + (Url?.Length ?? 0));
}
