using System.Globalization;

namespace Capacitor.Cli.Core.Auth;

/// <summary>Whether the account that authenticated is the account that approved.
/// <see cref="Indeterminate"/> is deliberately not <see cref="Mismatch"/>: "I could not tell" and
/// "it was somebody else" send the user to different remedies.</summary>
public enum PairingContinuity { Match, Mismatch, Indeterminate }

/// <summary>
/// The CLI's half of the pairing continuity check.
///
/// <para>The channel hands over a human's <b>approval</b>, never a credential, so the CLI still runs
/// its own login afterwards and nothing else binds the human who approved to the identity that then
/// authenticates.</para>
///
/// <para>The comparison is a <b>consistency guard on an honest client</b>, not a security boundary.
/// The server re-checks it at <c>/complete</c>, but that route is anonymous and its check only runs
/// when a bearer is presented, so a build that omits the header — or never calls <c>/complete</c> at
/// all — is not stopped by it. What it does buy is real: an honest CLI cannot silently finish setup
/// against the wrong account.</para>
/// </summary>
public static class PairingIdentity {
    const string GitHubPrefix = "github:";

    /// <summary>
    /// The canonical user id an access token represents, or null if it carries none.
    ///
    /// <para>Claim order follows what each provider actually mints, which is <b>not</b> what the
    /// server's own <c>ClaimsPrincipal</c> carries: <c>kapacitor:user_id</c> is added by a claims
    /// transformation at request time and never goes on the wire. A tenant-minted GitHub JWT carries
    /// <c>github_id</c> and a <c>sub</c> of <c>github|{n}</c>; a WorkOS access token carries the
    /// canonical id as <c>sub</c> verbatim.</para>
    /// </summary>
    public static string? FromAccessToken(string? accessToken) {
        using var payload = JwtPayload.TryRead(accessToken);

        if (payload is null) return null;

        var root = payload.RootElement;

        // kapacitor:user_id first only so a future server that does put it on the wire wins; no
        // token in the field carries it today.
        if (root.Str("kapacitor:user_id") is { Length: > 0 } canonical) return Normalize(canonical);

        if (root.Str("github_id") is { Length: > 0 } gitHubId) return Normalize(gitHubId);

        return root.Str("sub") is { Length: > 0 } sub ? Normalize(sub) : null;
    }

    /// <summary>
    /// Mirrors the server's <c>UserIds.Normalize</c>, plus the JWT's own <c>github|{n}</c> subject
    /// form — the tenant writes the pipe into the token and the colon into <c>approved_by</c>, so
    /// comparing the two raw is a guaranteed false mismatch for the same person.
    /// </summary>
    public static string Normalize(string id) {
        if (id.StartsWith("github|", StringComparison.Ordinal)) return GitHubPrefix + id["github|".Length..];

        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var gitHubId)
            ? GitHubPrefix + gitHubId.ToString(CultureInfo.InvariantCulture)
            : id;
    }

    /// <summary>
    /// Compares the approver the server named against the identity this token represents.
    ///
    /// <para>An unreadable token is <see cref="PairingContinuity.Indeterminate"/>, never a match —
    /// an id that could not be determined is not evidence of agreement. It is not a mismatch either:
    /// telling someone a colleague approved their machine, when the real fault is a build that
    /// cannot read its own token, sends them to the wrong remedy entirely.</para>
    /// </summary>
    public static PairingContinuity Compare(string? expectedUserId, string? accessToken) {
        if (string.IsNullOrWhiteSpace(expectedUserId)) return PairingContinuity.Indeterminate;

        if (FromAccessToken(accessToken) is not { } actual) return PairingContinuity.Indeterminate;

        return string.Equals(Normalize(expectedUserId), actual, StringComparison.Ordinal)
            ? PairingContinuity.Match
            : PairingContinuity.Mismatch;
    }
}
