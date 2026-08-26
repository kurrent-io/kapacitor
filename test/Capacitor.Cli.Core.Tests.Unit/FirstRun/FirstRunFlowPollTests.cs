using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// Every branch of the poll's decision, without a socket. The point of extracting it: a poll that
// reads every unexpected response as "keep waiting" spins to timeout, which a user cannot tell from
// a flow that was never going to finish.
public class FirstRunFlowPollTests {
    [Test]
    [Arguments(200, true,  FirstRunPollVerdict.State)]
    [Arguments(200, false, FirstRunPollVerdict.Wait)]           // answered, body unreadable — a blip
    [Arguments(404, true,  FirstRunPollVerdict.Gone)]
    [Arguments(410, true,  FirstRunPollVerdict.Expired)]
    [Arguments(429, true,  FirstRunPollVerdict.SlowDown)]
    [Arguments(401, true,  FirstRunPollVerdict.Unauthenticated)]
    [Arguments(403, true,  FirstRunPollVerdict.Unauthenticated)]
    [Arguments(408, true,  FirstRunPollVerdict.Wait)]
    [Arguments(400, true,  FirstRunPollVerdict.Gone)]
    [Arguments(500, true,  FirstRunPollVerdict.Wait)]
    [Arguments(0,   false, FirstRunPollVerdict.Wait)]           // transport
    public async Task Classifies(int status, bool bodyRead, FirstRunPollVerdict expected) =>
        await Assert.That(FirstRunFlowPoll.Classify(status, bodyRead)).IsEqualTo(expected);

    [Test]
    public async Task Reads_404_as_gone_rather_than_forbidden() {
        // The server answers 404 for a flow owned by someone else, deliberately indistinguishable
        // from an unknown id — a 403 would confirm the id exists, and the id is the only thing an
        // attacker would have had to guess. Treating it as anything retryable would spin against a
        // flow that will never be ours.
        await Assert.That(FirstRunFlowPoll.Classify(404, false)).IsEqualTo(FirstRunPollVerdict.Gone);
    }
}
