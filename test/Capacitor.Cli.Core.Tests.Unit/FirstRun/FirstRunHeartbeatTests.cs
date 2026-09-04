using Capacitor.Cli.Core.FirstRun;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

/// <summary>
/// The beat that turns a machine's death into something the server can observe. Driven over a fake
/// channel and a FakeTimeProvider, so none of it needs a socket or a wall clock.
///
/// <para>Waits are bounded by a real deadline rather than a fixed number of yields: the beat runs on its
/// own task, and a loaded runner can starve a continuation for longer than any yield budget.</para>
/// </summary>
public class FirstRunHeartbeatTests {
    const string Server = "https://acme.kcap.ai";
    const string Flow   = "flow-a";

    static readonly TimeSpan Beat = TimeSpan.FromSeconds(5);

    /// <summary>Generous, and only ever waited out in full by a failing test.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    sealed class FakeChannel : IFirstRunFlowChannel {
        readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int _beats;

        public int Beats => Volatile.Read(ref _beats);

        /// <summary>Holds every beat until <see cref="Release"/>, to keep one provably in flight.</summary>
        public bool Block { get; init; }

        public Exception? Throws { get; set; }

        /// <summary>Answered to the next beat, then cleared. Models a server that throttles once.</summary>
        public FirstRunHeartbeatOutcome? Next { get; set; }

        /// <summary>Answered to every beat. Models a route that is simply not there.</summary>
        public FirstRunHeartbeatOutcome? Always { get; set; }

        /// <summary>Never completes, so a beat only ends when its own bound cancels it.</summary>
        public bool BlockForever { get; init; }

        public List<CancellationToken> Tokens { get; } = [];

        public void Release() => _gate.TrySetResult();

        public async Task<FirstRunHeartbeatOutcome> HeartbeatAsync(
                string serverUrl, string flowId, CancellationToken ct) {
            Interlocked.Increment(ref _beats);

            lock (Tokens) Tokens.Add(ct);

            if (BlockForever) await Task.Delay(Timeout.Infinite, ct);
            if (Block) await _gate.Task;
            if (Throws is { } boom) throw boom;

            if (Next is { } once) {
                Next = null;

                return once;
            }

            return Always ?? new(204);
        }

        public Task<FirstRunCreateOutcome> CreateAsync(string s, string f, FirstRunMachineReport r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunPollOutcome> PollAsync(string s, string f, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunActionReportOutcome> ReportMachineActionAsync(string s, string f, ReportFirstRunMachineActionRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportAsync(string s, string f, ReportFirstRunImportRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(string s, string f, ReportFirstRunImportOutcomeRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunRelinquishOutcome> RelinquishAsync(string s, string f, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Advances the fake clock until the beat count reaches <paramref name="target"/>, or the
    /// real deadline passes. Re-advancing is safe: a timer that has already fired ignores it.</summary>
    static async Task<bool> ReachesAsync(FakeChannel channel, FakeTimeProvider clock, int target) {
        var until = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < until) {
            if (channel.Beats >= target) return true;

            clock.Advance(Beat);

            await Task.Delay(5);
        }

        return channel.Beats >= target;
    }

    /// <summary>
    /// Without this the server hears nothing for a whole interval after the browser opens, and a flow
    /// that dies inside it is indistinguishable from one that never beat — which reads as Unknown, and
    /// so as no problem at all.
    /// </summary>
    [Test]
    public async Task It_beats_as_soon_as_it_starts() {
        var channel = new FakeChannel();
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(channel.Beats).IsEqualTo(1);
    }

    [Test]
    public async Task It_goes_on_beating() {
        var channel = new FakeChannel();
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(await ReachesAsync(channel, clock, 3)).IsTrue()
                    .Because($"the beat stopped after {channel.Beats}");
    }

    /// <summary>
    /// The failure this exists to survive. A beat is a network call on a machine whose network is the
    /// very thing that may be failing, so one that ends the loop would convert a blip into a permanent
    /// silence — the loop would stop exactly when it was most needed.
    /// </summary>
    [Test]
    public async Task A_channel_that_throws_does_not_stop_the_beat() {
        var channel = new FakeChannel { Throws = new InvalidOperationException("boom") };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(await ReachesAsync(channel, clock, 3)).IsTrue()
                    .Because($"a throwing beat killed the loop after {channel.Beats}");
    }

    [Test]
    public async Task Disposing_it_stops_the_beats() {
        var channel = new FakeChannel();
        var clock   = new FakeTimeProvider();

        var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(await ReachesAsync(channel, clock, 2)).IsTrue()
                    .Because("a beat that never started would make the count below hold trivially");

        beat.Dispose();

        var settled = channel.Beats;

        for (var i = 0; i < 5; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        await Assert.That(channel.Beats).IsEqualTo(settled);
    }

    /// <summary>
    /// A beat already in flight is deliberately NOT waited for. It was issued while the machine was
    /// alive, so it reports something true, and the relinquish that follows closes the flow whatever
    /// order the two land in — the browser reads a stated ending ahead of an inferred one.
    /// </summary>
    [Test]
    public async Task Disposing_it_does_not_wait_for_a_beat_in_flight() {
        var channel = new FakeChannel { Block = true };
        var clock   = new FakeTimeProvider();

        var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        beat.Dispose();

        await Assert.That(channel.Beats).IsEqualTo(1)
                    .Because("the held beat was issued before the stop and stays issued");

        channel.Release();
    }

    [Test]
    public async Task Disposing_it_twice_is_harmless() {
        var channel = new FakeChannel();
        var clock   = new FakeTimeProvider();

        var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        beat.Dispose();
        beat.Dispose();

        await Assert.That(channel.Beats).IsEqualTo(1);
    }

    /// <summary>
    /// A hung beat is never cancelled, and never joined by a second.
    ///
    /// <para><b>The no-cancel half is the load-bearing one.</b> The beat rides the setup client, whose
    /// 401 handler rotates a single-use refresh token and then persists it, the rotation itself being
    /// uncancellable. A cancel landing between the two spends the credential server-side and never writes
    /// the replacement, logging the user out mid-setup — so the stop token is kept off the request
    /// entirely and the client's own timeout is what ends a beat.</para>
    ///
    /// <para>The no-second half is the cost of that: an outstanding beat holds the lane, so a wedged
    /// network is silent until the request times out rather than piling up one open POST per interval on
    /// the very machine whose network is failing.</para>
    /// </summary>
    [Test]
    public async Task A_hung_beat_is_neither_cancelled_nor_joined() {
        var channel = new FakeChannel { BlockForever = true };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        for (var i = 0; i < 10; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        await Assert.That(channel.Beats).IsEqualTo(1)
                    .Because("beats piled up behind a hung one, on the machine whose network is failing");

        CancellationToken first;

        lock (channel.Tokens) first = channel.Tokens[0];

        await Assert.That(first.CanBeCanceled).IsFalse()
                    .Because("the request was issued with a cancellable token, which is what can strand "
                           + "a rotated credential when the stop lands mid-recovery");
    }

    /// <summary>
    /// A 429 is an instruction, not a failure, and it is one of the two statuses this beat reads.
    /// Beating through it would spend a throttled tenant's budget on liveness and leave the poll — the
    /// half a human is waiting on — in penalty.
    /// </summary>
    [Test]
    public async Task A_throttled_beat_goes_quiet_for_as_long_as_it_was_told() {
        var channel = new FakeChannel { Next = new FirstRunHeartbeatOutcome(429, TimeSpan.FromSeconds(90)) };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(channel.Beats).IsEqualTo(1);

        for (var i = 0; i < 10; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        await Assert.That(channel.Beats).IsEqualTo(1)
                    .Because("the beat kept posting through a throttle it had been given a delay for");

        clock.Advance(TimeSpan.FromSeconds(90));

        await Assert.That(await ReachesAsync(channel, clock, 2)).IsTrue()
                    .Because("the beat never resumed after the throttle window passed");
    }

    /// <summary>
    /// The heartbeat has its own limiter, so the poll can be answering every 2s while this route is
    /// refused. An unclamped delay would then tell the browser the machine had gone for longer than the
    /// leg itself lasts, with a working connection either side of it.
    /// </summary>
    [Test]
    public async Task A_throttle_longer_than_the_leg_is_clamped() {
        var channel = new FakeChannel { Next = new FirstRunHeartbeatOutcome(429, TimeSpan.FromHours(1)) };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        clock.Advance(TimeSpan.FromMinutes(3));

        await Assert.That(await ReachesAsync(channel, clock, 2)).IsTrue()
                    .Because("an hour-long Retry-After was honoured in full, outlasting the whole leg");
    }

    /// <summary>
    /// A route this server does not have answers the same way for the rest of the leg. Beating on is
    /// hundreds of authenticated no-ops per run, which can trip the very limiter the throttle handling
    /// exists to keep clear for the poll. The create path reads 404 the same way.
    /// </summary>
    [Test]
    [Arguments(404)]
    [Arguments(405)]
    public async Task A_route_that_is_never_there_stops_the_beat(int status) {
        var channel = new FakeChannel { Always = new FirstRunHeartbeatOutcome(status) };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        for (var i = 0; i < 20; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        await Assert.That(channel.Beats).IsLessThanOrEqualTo(4)
                    .Because("the beat went on posting to a route the server does not have");
    }

    /// <summary>
    /// One refusal is not the route being absent. A gateway 405 during a rolling deploy, or a proxy
    /// rejecting POST on a path it has not learned, would otherwise silence liveness for the rest of the
    /// leg while the poll keeps succeeding on the same client — the browser inferring the machine has
    /// gone from one that is demonstrably still talking to it.
    /// </summary>
    [Test]
    public async Task A_single_not_found_does_not_stop_the_beat() {
        var channel = new FakeChannel { Next = new FirstRunHeartbeatOutcome(404) };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        await Assert.That(await ReachesAsync(channel, clock, 4)).IsTrue()
                    .Because($"a single 404 ended the beat; it reached {channel.Beats}");
    }
}
