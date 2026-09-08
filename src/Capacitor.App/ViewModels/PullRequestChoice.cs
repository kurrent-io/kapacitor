using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record PullRequestChoice(PullRequestLinkDto Link, bool IsAvailable = true) {
    public PullRequestSubjectDto Subject { get; } = PullRequestWire.Subject(Link);
    public string Label => $"{Link.Owner}/{Link.RepoName} #{Link.Number}" + (IsAvailable ? "" : " · Unavailable");
    public override string ToString() => Label;
}
