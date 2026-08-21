namespace Capacitor.Cli.Core;

/// <summary>
/// The single acceptance predicate every hook-path guard consults.
///
/// <para>Lives beside <see cref="HttpClientExtensions.IsAcceptableUrl"/> deliberately: a guard that
/// can disagree with the validator it protects is worse than no guard, because it fails in exactly
/// the case it was added for. Before this existed the same composition was hand-written in twelve
/// places and named twice.</para>
/// </summary>
public static class HookHttp {
    /// <summary>
    /// Whether a caller may attempt auth discovery for <paramref name="baseUrl"/> at all.
    /// False for null, blank, scheme-less, relative, and absolute non-http(s) URLs.
    /// </summary>
    public static bool IsPostable(string? baseUrl)
        => !string.IsNullOrWhiteSpace(baseUrl) && HttpClientExtensions.IsAcceptableUrl(baseUrl);
}
