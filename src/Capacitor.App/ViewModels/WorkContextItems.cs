using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum WorkContextPartMark { Settled, ThisSession, Unknown }

/// One declared part: settled by the server's milestone, else the one this session is attached
/// to, else unknown.
public sealed class WorkContextPartViewModel(string title, WorkContextPartMark mark) {
    public string Title { get; } = title;
    public WorkContextPartMark Mark { get; } = mark;
    public bool IsSettled => Mark == WorkContextPartMark.Settled;
    public bool IsThisSession => Mark == WorkContextPartMark.ThisSession;
}

/// A pull-request or issue card. The URL is server-returned, so it crosses the same trust boundary
/// the chat tab applies before a link reaches the shell opener.
public sealed class WorkContextLinkViewModel {
    public string  Eyebrow { get; }
    public string  Key     { get; }
    public string  Title   { get; }
    public string? Url     { get; }
    public bool    CanOpen { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public WorkContextLinkViewModel(string eyebrow, string key, string title, string? url, IUrlOpener opener) {
        Eyebrow = eyebrow;
        Key     = key;
        Title   = title;
        Url     = url;
        CanOpen = LinkPolicy.IsOpenable(url);
        OpenCommand = ReactiveCommand.Create(() => LinkPolicy.Open(opener, url), Observable.Return(CanOpen));
    }
}

/// One person with a session attached to the work item. <see cref="AvatarUrl"/> is carried but not
/// loaded: the pane draws the initial.
public sealed class WorkContextPersonViewModel {
    public string  Name             { get; }
    public string  Initial          { get; }
    public string? AvatarUrl        { get; }
    public string  LastActivityText { get; }

    public WorkContextPersonViewModel(string name, string? avatarUrl, DateTimeOffset? lastActivityAt, DateTimeOffset now) {
        Name             = name;
        Initial          = InitialOf(name);
        AvatarUrl        = avatarUrl;
        LastActivityText = lastActivityAt is { } at ? RelativeTime.Format(at, now) : "";
    }

    /// The first text element, so a surrogate pair or a combining sequence stays whole.
    public static string InitialOf(string name) => new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
}
