namespace Capacitor.App.Services;

/// The trust boundary for agent-authored links: the shell opener launches whatever it is
/// handed, so only absolute web URLs ever reach it.
public static class LinkPolicy {
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
