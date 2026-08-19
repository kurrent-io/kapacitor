using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// Pure classification of one pairing status poll. Kept out of the flow so every branch is covered
// without a server, including the ones that must NOT be treated as "keep waiting".
public class PairingPollTests {
    [Test]
    public async Task Approved_completes() =>
        await Assert.That(PairingPoll.Classify(200, "approved")).IsEqualTo(PairingVerdict.Approved);

    // A human refusing is an answer, not a fault: retrying would re-ask a question already settled,
    // and phrasing it as an error tells someone who correctly spotted a phishing attempt they failed.
    [Test]
    public async Task Denied_is_terminal_and_distinct_from_failure() =>
        await Assert.That(PairingPoll.Classify(200, "denied")).IsEqualTo(PairingVerdict.Denied);

    [Test]
    public async Task Pending_keeps_waiting() =>
        await Assert.That(PairingPoll.Classify(200, "pending")).IsEqualTo(PairingVerdict.Wait);

    // 'completed' is deliberately absent from the poll vocabulary — completion invalidates the
    // secret, so a caller that could observe it no longer authenticates. Treat it as unremarkable.
    [Test]
    public async Task An_unknown_status_keeps_waiting() =>
        await Assert.That(PairingPoll.Classify(200, "completed")).IsEqualTo(PairingVerdict.Wait);

    [Test]
    public async Task Gone_is_expiry() =>
        await Assert.That(PairingPoll.Classify(410, null)).IsEqualTo(PairingVerdict.Expired);

    // Unlike the provisioning poll, there is no token source to refresh on the next tick: the secret
    // was minted once and never changes, so a server rejecting it now rejects it forever.
    [Test]
    public async Task Unauthorized_is_terminal() =>
        await Assert.That(PairingPoll.Classify(401, null)).IsEqualTo(PairingVerdict.Gone);

    [Test]
    public async Task NotFound_is_terminal() =>
        await Assert.That(PairingPoll.Classify(404, null)).IsEqualTo(PairingVerdict.Gone);

    [Test]
    public async Task TooManyRequests_backs_off_rather_than_aborting() =>
        await Assert.That(PairingPoll.Classify(429, null)).IsEqualTo(PairingVerdict.SlowDown);

    [Test]
    public async Task Transport_failure_keeps_waiting() =>
        await Assert.That(PairingPoll.Classify(0, null)).IsEqualTo(PairingVerdict.Wait);

    [Test]
    public async Task Server_error_keeps_waiting() =>
        await Assert.That(PairingPoll.Classify(503, null)).IsEqualTo(PairingVerdict.Wait);

    // A 200 whose body could not be read arrives as a null status. Reading that as an answer would
    // let an unparseable response end the flow.
    [Test]
    public async Task An_unreadable_body_keeps_waiting() =>
        await Assert.That(PairingPoll.Classify(200, null)).IsEqualTo(PairingVerdict.Wait);
}
