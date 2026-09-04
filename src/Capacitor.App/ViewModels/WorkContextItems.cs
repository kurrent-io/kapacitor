using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum WorkContextPartMark { ThisSession, Unknown }

/// One declared part. The server exposes no completion state over HTTP, so a part is either the
/// one this session is attached to or unknown.
public sealed class WorkContextPartViewModel(string title, WorkContextPartMark mark) {
    public string Title { get; } = title;
    public WorkContextPartMark Mark { get; } = mark;
    public bool IsThisSession => Mark == WorkContextPartMark.ThisSession;
}

/// A pull-request card. The URL is server-returned, so it crosses the same trust boundary the chat
/// tab applies before a link reaches the shell opener.
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
