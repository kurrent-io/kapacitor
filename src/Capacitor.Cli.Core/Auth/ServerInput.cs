namespace Capacitor.Cli.Core.Auth;

// Normalizes user-supplied server input for `kcap setup`/`kcap login` and the desktop app's
// onboarding facade — no network access, string shaping only.
public static class ServerInput {
    /// <summary>
    /// Resolves a `kcap setup &lt;tenant&gt;` positional: a bare single label (no scheme/dot/port)
    /// expands to <c>https://{slug}.kcap.ai</c>; anything that already looks like a URL, FQDN, or
    /// host:port is returned unchanged for the normal --server-url path. Self-hosted servers should
    /// pass a full URL.
    /// </summary>
    /// <summary>
    /// Reduces a user-supplied server to its origin, dropping any path/query/fragment. Everything
    /// downstream appends a fixed root path (<c>/auth/config</c>), so a pasted page URL would probe
    /// the wrong endpoint and be reported unreachable. Applied to the zero-discovery
    /// "I already have a workspace" input, which explicitly invites a paste; the pre-existing
    /// <c>--server-url</c> / <c>&lt;tenant&gt;</c> arguments keep their current behaviour. A bare
    /// slug passes through untouched for <see cref="ResolveTenantArg"/> to expand.
    /// </summary>
    public static string ToServerOrigin(string input) {
        var trimmed = input.Trim().TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
         && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)) {
            return absolute.GetLeftPart(UriPartial.Authority);
        }

        // Scheme-less: cut at the first path/query/fragment separator. A bracketed IPv6 literal
        // ("[::1]:5108") must be skipped first or the scan would cut inside the address.
        var bracketEnd = trimmed.StartsWith('[') ? trimmed.IndexOf(']') : -1;
        var scanFrom   = bracketEnd > 0 ? bracketEnd + 1 : 0;
        var cut        = trimmed.IndexOfAny(['/', '?', '#'], scanFrom);

        return cut < 0 ? trimmed : trimmed[..cut].TrimEnd('/');
    }

    public static string ResolveTenantArg(string arg) =>
        arg.Contains("://") || arg.Contains('.') || arg.Contains(':')
        || arg.Equals("localhost", StringComparison.OrdinalIgnoreCase) // bare loopback host, not a kcap.ai slug
            ? arg
            : $"https://{arg}.kcap.ai";
}
