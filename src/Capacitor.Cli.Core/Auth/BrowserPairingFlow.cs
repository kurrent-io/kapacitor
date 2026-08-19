namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Mints a pairing, shows the code, opens the browser, and waits for a human.
///
/// <para><b>The code is printed in the terminal, always.</b> The defence is a human comparing what
/// the browser shows against what the machine shows, so a browser code with nothing to check it
/// against is theatre. The fallback URL is printed for the reason <see cref="SystemBrowser"/> gives.</para>
///
/// <para>This flow carries no credential — see <see cref="PairingIdentity"/> for what the caller
/// still owes once it returns <see cref="PairingResult.Approved"/>.</para>
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

    /// <summary>Floor under the locally-measured budget. <c>expires_at</c> is the SERVER's wall
    /// clock; comparing it against ours means a machine whose clock runs fast — a resumed VM, a dead
    /// CMOS battery — computes a deadline already in the past and gives up without polling once.
    /// Measuring locally from a floor makes skew irrelevant, and costs nothing: the server still
    /// answers 410 the moment it really has expired.</summary>
    static readonly TimeSpan MinPollBudget = TimeSpan.FromMinutes(16);

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

        if (minted.Body is not { } pairing
         || string.IsNullOrEmpty(pairing.Secret)
         || string.IsNullOrEmpty(pairing.PairingId)
         || string.IsNullOrEmpty(pairing.SetupUrl)
         || pairing.ExpiresAt == default)
            return new PairingResult.Failed(
                minted.StatusCode == 0
                    ? "Could not reach the server to start setup."
                    : $"The server did not return a usable setup request (HTTP {minted.StatusCode}).");

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
        var deadline   = _clock.GetUtcNow() + Max(advertised, MinPollBudget) + PollGrace;
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

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
