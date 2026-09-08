using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record PullRequestChoice(PullRequestLinkDto Link) {
    public PullRequestSubjectDto Subject { get; } = PullRequestWire.Subject(Link);
    public string Label => $"{Link.Owner}/{Link.RepoName} #{Link.Number}";
    public override string ToString() => Label;
}
