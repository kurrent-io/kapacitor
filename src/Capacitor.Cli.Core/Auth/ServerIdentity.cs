namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Canonical identity for a Capacitor server URL — the comparison used to decide whether a
/// stored token may be presented to a given server.
///
/// Whole-string comparison is not safe here: <c>https://x</c> and <c>https://x:443</c> are the
/// same server, host casing is irrelevant, but a path IS significant (path-routed deployments)
/// and is case-sensitive per HTTP. A configured server URL is a request base, so userinfo,
/// query and fragment are rejected outright rather than silently dropped — dropping them would
/// make <c>https://host/base?tenant=a</c> and <c>?tenant=b</c> compare equal.
/// </summary>
public static class ServerIdentity {
    /// <summary>
    /// Canonical form of <paramref name="url"/>, or <c>null</c> when it is not an admissible
    /// server base (unparseable, relative, non-http(s), or carrying userinfo/query/fragment).
    /// </summary>
    public static string? Canonicalize(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (!string.IsNullOrEmpty(uri.Query)) return null;
        if (!string.IsNullOrEmpty(uri.Fragment)) return null;

        // Uri lowercases scheme and host (and IDN-normalizes the host) for us; Port is the
        // effective port, so an implicit and an explicit default port converge here.
        var path = uri.AbsolutePath.TrimEnd('/');

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
    }

    /// <summary>
    /// Canonical form for stamping onto a freshly minted token, or an error message naming the
    /// problem. A null <c>ServerUrl</c> means "pre-upgrade token, binding unenforced", so a NEW
    /// token must never be saved with one: silently storing null here would downgrade a token we
    /// could have bound into one that any server is allowed to receive.
    /// </summary>
    public static bool TryCanonicalizeForStamping(string? url, out string canonical, out string error) {
        var result = Canonicalize(url);

        if (result is null) {
            canonical = "";
            error     = $"Server URL '{url}' is not usable as a server identity — it must be an "
                      + "absolute http(s) URL with no user info, query string, or fragment.";

            return false;
        }

        canonical = result;
        error     = "";

        return true;
    }

    /// <summary>
    /// True when both sides name the same server. A non-canonicalizable side never matches:
    /// we fail closed to "no binding assertion can be made" rather than to a false match.
    /// </summary>
    public static bool SameServer(string? left, string? right) {
        var a = Canonicalize(left);
        var b = Canonicalize(right);

        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one identity comparison every boot/gate/consent-IPC identity check should use instead
    /// of an ad-hoc <c>TrimEnd('/')</c> + <c>OrdinalIgnoreCase</c> compare. Both sides null/empty is
    /// agreement (no expectation configured on either side); exactly one empty is a mismatch —
    /// unlike <see cref="Canonicalize"/>'s callers that treat an absent expectation as trivially
    /// satisfied, THAT short-circuit belongs at the call site, not here. Otherwise both sides are
    /// canonicalized (scheme/host normalized, default ports converged, path case preserved) and
    /// compared ordinally.
    /// </summary>
    public static bool Matches(string? a, string? b) {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

        var ca = Canonicalize(a);
        var cb = Canonicalize(b);

        return ca is not null && cb is not null && string.Equals(ca, cb, StringComparison.Ordinal);
    }
}
