using System.Globalization;
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// The CLI's half of the pairing continuity check.
///
/// <para>The pairing hands over a human's <b>approval</b>, never a credential, so the CLI still runs
/// its own ordinary login afterwards. Nothing in the channel binds the human who approved to the
/// identity that then authenticates — this comparison is that binding, and without it a pairing
/// approved by one person could be redeemed by a session belonging to someone else.</para>
///
/// <para>Read from the access token rather than from a round trip: the tenant mints the GitHub JWT
/// itself and stamps <c>kapacitor:user_id</c> into it, and for WorkOS the server's own
/// transformation uses the token's <c>sub</c> verbatim. So the token already carries the answer the
/// server would give. <b>This is not a validation</b> — the CLI is reading a token it was handed, not
/// trusting one it received. The server re-checks the same comparison at <c>/complete</c> and
/// answers 403, which is what makes the rule enforceable against an old or modified build.</para>
/// </summary>
public static class PairingIdentity {
    const string UserIdClaim   = "kapacitor:user_id";
    const string GitHubIdClaim = "kapacitor:github_id"; // pre-rekey tokens carry only this
    const string GitHubPrefix  = "github:";

    /// <summary>
    /// The canonical user id an access token represents, or null if it carries none.
    ///
    /// <para>Order matters: <c>sub</c> is the fallback rather than the first choice, because a
    /// GitHub JWT's <c>sub</c> is a username while its <c>kapacitor:user_id</c> is the canonical id
    /// the server compares against.</para>
    /// </summary>
    public static string? FromAccessToken(string? accessToken) {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        var parts = accessToken.Split('.');

        if (parts.Length < 2) return null;

        JsonDocument payload;

        try {
            payload = JsonDocument.Parse(DecodeSegment(parts[1]));
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }

        using (payload) {
            var claim = ReadString(payload.RootElement, UserIdClaim)
                     ?? ReadString(payload.RootElement, GitHubIdClaim)
                     ?? ReadString(payload.RootElement, "sub");

            return claim is null ? null : Normalize(claim);
        }
    }

    /// <summary>Mirrors the server's <c>UserIds.Normalize</c>: a bare numeric id is a legacy GitHub
    /// id, and comparing it raw against a canonical one would false-mismatch the same person.</summary>
    public static string Normalize(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var gitHubId)
            ? GitHubPrefix + gitHubId.ToString(CultureInfo.InvariantCulture)
            : id;

    /// <summary>
    /// Whether the identity that just authenticated is the one that approved the pairing.
    ///
    /// <para>Fails closed on an unreadable token: an id the CLI could not determine is not evidence
    /// that it matches. The one exception is a server that named no approver, which cannot happen —
    /// the tenant's schema carries a CHECK forbidding it — so an absent expectation means the
    /// response was not the one this check was written against, and that is refused too.</para>
    /// </summary>
    public static bool Matches(string? expectedUserId, string? accessToken) =>
        !string.IsNullOrWhiteSpace(expectedUserId)
     && FromAccessToken(accessToken) is { } actual
     && string.Equals(Normalize(expectedUserId), actual, StringComparison.Ordinal);

    static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static byte[] DecodeSegment(string segment) {
        // JWT segments are base64url with the padding stripped; Convert.FromBase64String needs both
        // back before it will look at the string at all.
        var padded = segment.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (3 - (padded.Length + 3) % 4) % 4, '='));
    }
}
