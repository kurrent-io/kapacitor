namespace Capacitor.Cli.Core;

/// <summary>Where the resolved server URL came from, so remediation can name the right thing to fix.</summary>
public enum UrlSource {
    /// <summary><c>--server-url</c>.</summary>
    CommandLine,

    /// <summary><c>KCAP_URL</c>.</summary>
    Environment,

    /// <summary>A profile's <c>server_url</c>, however that profile was selected.</summary>
    Profile,
}

/// <summary>
/// Builds the one stderr line a guard writes when it declines to use a server URL.
///
/// <para>Silence is not an option here: a typo in <c>server_url</c> otherwise produces a machine that
/// records nothing indefinitely, with no signal anywhere. Recovery also needs a later hook for the
/// same session, so the window in which this line is actionable is short.</para>
/// </summary>
public static class UnusableUrlDiagnostic {
    const int MaxRendered = 80;

    /// <summary>
    /// Renders an untrusted URL safe to print. A <c>server_url</c> may carry credentials, and an
    /// embedded newline would let it inject a fabricated line into a stream harnesses parse — so
    /// userinfo is dropped, control characters are stripped, and the result is length-capped.
    /// </summary>
    public static string Sanitize(string? rawUrl) {
        if (string.IsNullOrWhiteSpace(rawUrl)) return "(empty)";

        var stripped = new string(rawUrl.Where(c => !char.IsControl(c)).ToArray());

        // Drop user:pass@ — take the LAST '@' so an embedded one cannot smuggle credentials past.
        // Slice on ANY '@', including a trailing one: "https://user:secret@" is exactly the kind of
        // malformed value this path exists to render, and requiring a character after '@' left the
        // credential intact on it.
        var at = stripped.LastIndexOf('@');
        if (at >= 0) stripped = stripped[(at + 1)..];

        if (stripped.Length == 0) return "(empty)";

        return stripped.Length <= MaxRendered ? stripped : string.Concat(stripped.AsSpan(0, MaxRendered), "…");
    }

    /// <summary>
    /// One line naming the source, the sanitized value, and what happened to the payload.
    /// The remediation must match the source: <c>kcap config set server_url</c> does NOT repair a
    /// malformed <c>KCAP_URL</c> or <c>--server-url</c>, both of which outrank the profile.
    /// </summary>
    public static string Build(UrlSource source, string? rawUrl, string disposition) {
        var (name, fix) = source switch {
            UrlSource.CommandLine => ("--server-url",
                                      "Pass --server-url https://<host>."),
            UrlSource.Environment => ("KCAP_URL",
                                      "Set KCAP_URL to https://<host>, or unset it to use the configured profile."),
            _                     => ("server_url",
                                      "Run: kcap config set server_url https://<host>"),
        };

        return $"[kcap] {name} is not an absolute http(s) URL ({Sanitize(rawUrl)}) — {disposition}."
             + System.Environment.NewLine
             + $"       {fix}";
    }
}
