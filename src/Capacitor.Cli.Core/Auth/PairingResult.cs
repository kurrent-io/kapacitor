namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What the pairing leg produced. <see cref="Unavailable"/> is not a failure — it is a server that
/// does not offer the channel, and the caller carries on with the ordinary login.
/// </summary>
public abstract record PairingResult {
    /// <param name="UserId">The approving human. The caller MUST compare this against the identity
    /// its own login produces — see <see cref="PairingIdentity"/>.</param>
    /// <param name="ServerUrl">The server's own canonical origin, which is what later calls address.</param>
    public sealed record Approved(
        string ServerUrl,
        string UserId,
        string PairingId,
        string Secret) : PairingResult;

    public sealed record Denied : PairingResult;

    public sealed record Expired : PairingResult;

    public sealed record Unavailable : PairingResult;

    public sealed record Failed(string Message) : PairingResult;

    /// <summary>The channel answered, but not in a shape this build can trust — a missing approver,
    /// or a tenant other than the one being configured. Distinct from <see cref="Failed"/> because
    /// carrying on would mean skipping the identity check rather than never having started it.</summary>
    public sealed record Untrusted(string Message) : PairingResult;
}
