namespace Capacitor.Cli.Core.Auth;

/// <summary>One profile the commit boundary will publish, with the canonical server it is bound to.</summary>
public sealed record AuthIdentity(string Profile, string CanonicalServer);

/// <summary>
/// Outcome of an onboarding operation. <see cref="Cancelled"/> is strictly pre-boundary — once the
/// boundary is entered every publication runs to completion and the answer is
/// <see cref="Committed"/>, never a torn stop. <see cref="Retarget"/> is the WorkOS "I already have
/// a workspace" answer: nothing durable, and the caller resolves the input against its own server.
/// </summary>
public abstract record AuthResult {
    public sealed record Committed(
        string                      ActiveProfile,
        string                      CanonicalServer,
        string                      Provider,
        string?                     Username,
        IReadOnlyList<AuthIdentity> Published) : AuthResult;

    public sealed record Cancelled : AuthResult;

    /// <param name="Message">Already rendered through <see cref="IAuthProgress"/>; carried for callers that log or re-present it.</param>
    public sealed record Failed(string Message) : AuthResult;

    public sealed record Retarget(string ServerInput) : AuthResult;
}
