namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>The registry's surface beyond <see cref="IPullRequestSource"/>: the view model reaches it through an <c>as</c> cast so the server-only source stays untouched.</summary>
public interface IPullRequestReaders {
    void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch);
    PullRequestReaderNote? NoteFor(string provider, string host);
    string? PrLink(string? url, PullRequestSubjectDto subject);
}
