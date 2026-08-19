namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Mints a pairing, shows the code, opens the browser, and waits for a human.
///
/// <para>The defence is a human comparing the code on screen against the code in the terminal, so
/// every field that comparison rests on is validated before the browser opens, and the mint response
/// is treated as untrusted input throughout. See <see cref="PairingIdentity"/> for what the caller
/// still owes once this returns <see cref="PairingResult.Approved"/>.</para>
/// </summary>
public sealed class BrowserPairingFlow(
        IPairingChannel  channel,
        IPairingProgress progress,
        TimeProvider?    clock       = null,
        Action<string>?  openBrowser = null) {
    readonly TimeProvider   _clock       = clock ?? TimeProvider.System;
    readonly Action<string> _openBrowser = openBrowser ?? SystemBrowser.Open;

    /// <summary>How much longer than the pairing's own expiry to keep polling. The server owns the
    /// deadline and answers 410 when it passes; this only bounds the loop if that never arrives.</summary>
    static readonly TimeSpan PollGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Bounds on the locally-measured budget. <c>expires_at</c> is the SERVER's wall clock, so
    /// measuring against ours lets its errors become ours in both directions: a machine running fast
    /// computes a deadline already past and gives up without polling once, and a far-future expiry
    /// keeps an interactive command polling for months. Clamping makes both harmless — the server
    /// still answers 410 the moment it really has expired.
    /// </summary>
    static readonly TimeSpan MinPollBudget = TimeSpan.FromMinutes(16);

    static readonly TimeSpan MaxPollBudget = TimeSpan.FromHours(1);

    /// <summary>Applied on a 429. The server advertises an interval and enforces 80% of it, so
    /// arriving early is the client's fault and backing off is the whole remedy.</summary>
    static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(1);

    static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    public async Task<PairingResult> RunAsync(
            string serverUrl, string machineId, string machineName, CancellationToken ct) {
        var minted = await channel.MintAsync(serverUrl, machineId, machineName, ct);

        // Every one of these means "no pairing channel here" and every one has the same remedy —
        // carry on with the login that already worked. A gateway answering 401/403/405 on an
        // unmapped anonymous route is indistinguishable from the feature being off, and alarming the
        // user about it would be noise: by this point the origin has already been probed and its
        // /auth/config read, so the server is known to exist.
        if (minted.StatusCode is 404 or 401 or 403 or 405) return new PairingResult.Unavailable();

        // UserCode is in here for the same reason as the rest: an empty one renders a prompt with
        // nothing beside it, which removes the comparison silently rather than failing.
        if (minted.Body is not { } pairing
         || string.IsNullOrEmpty(pairing.Secret)
         || string.IsNullOrEmpty(pairing.PairingId)
         || string.IsNullOrEmpty(pairing.UserCode)
         || string.IsNullOrEmpty(pairing.SetupUrl)
         || pairing.ExpiresAt == default)
            return new PairingResult.Failed(
                minted.StatusCode == 0
                    ? "Could not reach the server to start setup."
                    : $"The server did not return a usable setup request (HTTP {minted.StatusCode}).");

        if (!IsOpenable(pairing.SetupUrl, serverUrl))
            return new PairingResult.Untrusted(
                $"The server asked to open {pairing.SetupUrl}, which is not a page on {serverUrl}.");

        progress.AwaitingApproval(pairing.UserCode, pairing.SetupUrl);
        _openBrowser(pairing.SetupUrl);

        try {
            return await PollAsync(serverUrl, pairing, ct);
        } finally {
            progress.WaitEnded();
        }
    }

    async Task<PairingResult> PollAsync(string serverUrl, MintPairingResponse pairing, CancellationToken ct) {
        // Clamped at both ends: the server names the interval, and a misconfigured one would
        // otherwise leave the terminal silent for however long it said.
        var interval = Min(TimeSpan.FromSeconds(Math.Max(1, pairing.PollIntervalSeconds)), MaxInterval);

        var advertised = pairing.ExpiresAt - _clock.GetUtcNow();
        var deadline   = _clock.GetUtcNow() + Min(Max(advertised, MinPollBudget), MaxPollBudget) + PollGrace;
        var first      = true;

        while (_clock.GetUtcNow() < deadline) {
            // Poll before the first sleep: a human who approves instantly should not wait out an
            // interval to be noticed. The server's floor is measured from the previous poll, so
            // there is nothing here to arrive early for.
            if (!first) await Task.Delay(interval, _clock, ct);

            first = false;

            var poll = await channel.PollAsync(serverUrl, pairing.PairingId, pairing.Secret, ct);

            switch (PairingPoll.Classify(poll.StatusCode, poll.Body?.Status)) {
                case PairingVerdict.Approved:
                    // Both are contractual on an approved poll. Missing either means this is not the
                    // response the flow was written against, and going on would skip the identity
                    // check the whole design rests on.
                    if (poll.Body?.ServerUrl is not { Length: > 0 } approvedServer
                     || poll.Body.User?.Id is not { Length: > 0 } approver)
                        return new PairingResult.Untrusted("The server approved setup but did not say who approved it.");

                    // The tenant that approved must be the tenant being configured. It always is on
                    // this path, which is exactly why a disagreement is worth stopping for.
                    if (!ServerIdentity.SameServer(approvedServer, serverUrl))
                        return new PairingResult.Untrusted(
                            $"Setup was approved on {approvedServer}, but this machine is being configured for {serverUrl}.");

                    return new PairingResult.Approved(approvedServer, approver, pairing.PairingId, pairing.Secret);

                case PairingVerdict.Denied:
                    return new PairingResult.Denied();

                case PairingVerdict.Expired:
                    return new PairingResult.Expired();

                case PairingVerdict.Gone:
                    return new PairingResult.Failed("The server no longer recognises this setup request.");

                case PairingVerdict.SlowDown:
                    interval = Min(interval + SlowDownStep, MaxInterval);
                    progress.PollTick();

                    break;

                default:
                    progress.PollTick();

                    break;
            }
        }

        return new PairingResult.Expired();
    }

    /// <summary>
    /// Whether a URL is safe to hand to the OS.
    ///
    /// <para>It goes to a shell-executed open, so scheme and origin are checked first: a
    /// <c>file://</c> path or a registered custom scheme would otherwise be launched on a server's
    /// say-so, and userinfo lets a URL read as one host while addressing another.</para>
    ///
    /// <para>Compared against the whole configured base, path included. A path-routed deployment
    /// puts the tenant IN the path, so reducing either side to its authority both admits a sibling
    /// tenant's page and rejects the legitimate one. <see cref="ServerIdentity"/> refuses a base
    /// carrying a query, which <c>setup_url</c> always has, so the query and fragment come off
    /// before canonicalising — the URL that is opened keeps them.</para>
    /// </summary>
    static bool IsOpenable(string setupUrl, string serverUrl) {
        if (!Uri.TryCreate(setupUrl, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;

        var target = ServerIdentity.Canonicalize($"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath}");
        var origin = ServerIdentity.Canonicalize(serverUrl);

        if (target is null || origin is null) return false;

        // The trailing separator is what stops base "/tenant-a" matching "/tenant-abc".
        return target == origin || target.StartsWith(origin + "/", StringComparison.Ordinal);
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
