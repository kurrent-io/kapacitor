namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What the pairing leg produced. <see cref="Unavailable"/> is not a failure — it is a tenant that
/// does not serve the channel, and the caller falls back to the ordinary login.
/// </summary>
public abstract record PairingResult {
    /// <param name="UserId">The approving human, which the caller MUST compare against the identity
    /// its own login then produces — see <see cref="PairingIdentity"/>.</param>
    public sealed record Approved(
        string ServerUrl,
        string UserId,
        string PairingId,
        string Secret) : PairingResult;

    public sealed record Denied : PairingResult;

    public sealed record Expired : PairingResult;

    public sealed record Unavailable : PairingResult;

    public sealed record Failed(string Message) : PairingResult;
}

/// <summary>What the pairing leg needs to show a human. Separate from <see cref="IAuthProgress"/>
/// because the code-comparison line is the one piece of copy the flow's security depends on, and it
/// has no analogue in any other auth path.</summary>
public interface IPairingProgress {
    /// <summary>The code and the fallback link. <b>Both are mandatory</b> — see
    /// <see cref="BrowserPairingFlow"/>.</summary>
    void AwaitingApproval(string userCode, string setupUrl);

    /// <summary>One poll came back still pending.</summary>
    void PollTick();

    void Notice(string message);
}

/// <summary>
/// Mints a pairing, shows the code, opens the browser, and waits for a human.
///
/// <para><b>The code is printed in the terminal, always.</b> The whole defence is a human comparing
/// what the browser shows against what the machine shows; a browser that displays a code with
/// nothing to check it against is theatre. The fallback URL is printed for the same reason the
/// opener is best-effort — see <see cref="SystemBrowser"/>.</para>
///
/// <para><b>This flow carries no credential.</b> It collects a human's approval and the identity
/// that gave it; the caller then runs its ordinary login and asserts the two identities match. That
/// assertion is not optional and is the only thing binding the approver to the authenticated user.</para>
/// </summary>
public sealed class BrowserPairingFlow(
        PairingClient     client,
        IPairingProgress  progress,
        TimeProvider?     clock       = null,
        Action<string>?   openBrowser = null) {
    readonly TimeProvider   _clock       = clock ?? TimeProvider.System;
    readonly Action<string> _openBrowser = openBrowser ?? SystemBrowser.Open;

    /// <summary>How much longer than the pairing's own expiry to keep polling. The server is the
    /// authority on the deadline and answers 410 when it passes; this only stops an unbounded loop
    /// if that answer never arrives.</summary>
    static readonly TimeSpan PollGrace = TimeSpan.FromMinutes(1);

    /// <summary>Applied when the server says 429. It advertises an interval and enforces 80% of it,
    /// so arriving early is the client's fault and backing off is the whole remedy.</summary>
    static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(1);

    static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    public async Task<PairingResult> RunAsync(
            string serverUrl, string machineId, string machineName, CancellationToken ct) {
        var minted = await client.MintAsync(serverUrl, machineId, machineName, ct);

        // 404 means this tenant does not map the pairing routes at all. Not an error to report: the
        // caller has a working login path and the user has nothing to fix.
        if (minted.StatusCode == 404) return new PairingResult.Unavailable();

        if (minted.Body is not { } pairing || string.IsNullOrEmpty(pairing.Secret))
            return new PairingResult.Failed(
                minted.StatusCode == 0
                    ? "Could not reach the server to start setup."
                    : $"The server refused to start setup (HTTP {minted.StatusCode}).");

        progress.AwaitingApproval(pairing.UserCode, pairing.SetupUrl);
        _openBrowser(pairing.SetupUrl);

        return await PollAsync(serverUrl, pairing, ct);
    }

    async Task<PairingResult> PollAsync(string serverUrl, MintPairingResponse pairing, CancellationToken ct) {
        var interval = TimeSpan.FromSeconds(Math.Max(1, pairing.PollIntervalSeconds));
        var deadline = pairing.ExpiresAt + PollGrace;

        while (_clock.GetUtcNow() < deadline) {
            await Task.Delay(interval, _clock, ct);

            var poll    = await client.PollAsync(serverUrl, pairing.PairingId, pairing.Secret, ct);
            var verdict = PairingPoll.Classify(poll.StatusCode, poll.Body?.Status);

            switch (verdict) {
                case PairingVerdict.Approved:
                    // Both are required by the contract on an approved poll. Missing either means the
                    // response is not the one this flow was written against, and continuing would
                    // skip the identity check that the whole design rests on.
                    if (poll.Body?.ServerUrl is not { Length: > 0 } approvedServer
                     || poll.Body.User?.Id is not { Length: > 0 } approver)
                        return new PairingResult.Failed("The server approved setup but did not say who approved it.");

                    return new PairingResult.Approved(approvedServer, approver, pairing.PairingId, pairing.Secret);

                case PairingVerdict.Denied:
                    return new PairingResult.Denied();

                case PairingVerdict.Expired:
                    return new PairingResult.Expired();

                case PairingVerdict.Gone:
                    return new PairingResult.Failed("The server no longer recognises this setup request.");

                case PairingVerdict.SlowDown:
                    interval = Min(interval + SlowDownStep, MaxInterval);

                    break;

                default:
                    progress.PollTick();

                    break;
            }
        }

        return new PairingResult.Expired();
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
