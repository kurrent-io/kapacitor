namespace Capacitor.App.Services.Mutation;

public enum MutationVerb { Install, Replace, StartVerified, DetachedStart }

public sealed record MutationRequest(
    MutationVerb Verb, string Profile, string CanonicalServer, string DaemonName);

public enum RecoverySurface { Takeover, Reinstall, Attention, Storage, None }

/// One classified result of a daemon-mutation attempt (spec §3/§4); the mutation lane maps every raw outcome onto exactly one case.
public abstract record MutationOutcome {
    public sealed record Succeeded : MutationOutcome;
    public sealed record SucceededAfterTimeout : MutationOutcome;
    public sealed record AttentionSkew(string Detail) : MutationOutcome;
    public sealed record AttentionRepair(string Detail) : MutationOutcome;
    public sealed record UnconfirmedNoAttach : MutationOutcome;
    public sealed record Refused(string Reason, RecoverySurface Surface) : MutationOutcome;
    public sealed record Failed(int ExitCode, string? Reason, RecoverySurface Surface) : MutationOutcome;
}

public sealed record OutcomeEnvelope(MutationRequest Request, MutationOutcome Outcome);

/// Maps a machine-readable reason token to a recovery surface (spec §3/§4 pinned tables); unknown tokens fail closed to Attention.
public static class ReasonRouting {
    public static RecoverySurface ForStartGate(string token) => token switch {
        "directive_missing" or "directive_invalid" or "identity_mismatch" or "foreign_binary" => RecoverySurface.Takeover,
        "package_inconsistent" => RecoverySurface.Reinstall,
        _ => RecoverySurface.Attention,
    };

    public static RecoverySurface ForDaemonStart(string token) => token switch {
        "package_inconsistent" => RecoverySurface.Reinstall,
        _ => RecoverySurface.Attention,
    };

    public static RecoverySurface ForBootRefusal(string token) => token switch {
        "server_expectation_mismatch" or "consent_seed_invalid" => RecoverySurface.Takeover,
        "consent_seed_unwritable" => RecoverySurface.Storage,
        _ => RecoverySurface.Attention,
    };
}
