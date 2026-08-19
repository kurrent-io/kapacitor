namespace Capacitor.Cli.Core.Auth;

/// <summary>What one status poll means for the flow.</summary>
public enum PairingVerdict {
    Approved,   // a human said yes — server_url and user are populated
    Denied,     // a human said no — terminal, and NOT an error to retry
    Expired,    // the pairing lapsed before anyone answered — terminal, re-runnable
    Gone,       // unknown id, or a secret this server does not accept — terminal
    SlowDown,   // polled faster than advertised; back off and keep waiting
    Wait        // still pending, or a transient blip the next tick recovers from
}

/// <summary>
/// The pure decision behind the pairing poll, extracted so every branch is unit-tested without a
/// server. Modelled on <see cref="ProvisioningPoll"/>, and on the same reasoning: a poll that treats
/// every unexpected response as "keep waiting" spins silently until timeout, which is
/// indistinguishable to the user from a flow that is never going to finish.
/// </summary>
public static class PairingPoll {
    public static PairingVerdict Classify(int statusCode, string? status) => statusCode switch {
        200 when status == "approved" => PairingVerdict.Approved,
        200 when status == "denied"   => PairingVerdict.Denied,
        410                           => PairingVerdict.Expired,
        // 401 is terminal, not transient: unlike the provisioning poll there is no token source to
        // refresh on the next tick — the secret was minted once and never changes, so a server that
        // rejects it now will reject it forever.
        401 or 404                    => PairingVerdict.Gone,
        429                           => PairingVerdict.SlowDown,
        // 200 pending, 5xx, and 0 (transport): the browser side may simply not have got there yet.
        _                             => PairingVerdict.Wait
    };
}
