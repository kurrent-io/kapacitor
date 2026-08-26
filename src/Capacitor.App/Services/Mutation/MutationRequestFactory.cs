using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Mutation;

/// The one place a MutationRequest is built from resolved profile/server identity: a caller that
/// cannot bind a canonical server never reaches the executor — it fails closed to Refused before
/// any MutationRequest exists. Canonicalization is idempotent, so callers may pass either a raw
/// configured URL or an already-canonical one.
public static class MutationRequestFactory {
    /// Non-null return means refused (no request built); null return means `request` is usable.
    public static MutationOutcome? TryBuild(
            MutationVerb verb, string? profileName, string? serverUrl, string daemonName, out MutationRequest? request) {
        var canonical = ServerIdentity.Canonicalize(serverUrl);
        if (string.IsNullOrWhiteSpace(profileName) || canonical is null) {
            request = null;
            return new MutationOutcome.Refused("no_server_configured", RecoverySurface.Attention);
        }

        request = new MutationRequest(verb, profileName, canonical, daemonName);
        return null;
    }
}
