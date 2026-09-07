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

        int _answered;

        /// <summary>Beats that have returned an outcome. Distinct from <see cref="Beats"/>, which counts
        /// them as they are issued — a test releasing held answers has to wait on this one before moving
        /// the clock, or the tick it spends can arrive before there is anything to harvest.</summary>
        public int Answered => Volatile.Read(ref _answered);

        /// <summary>Holds every beat until <see cref="Release"/>, to keep one provably in flight.</summary>
        public bool Block { get; init; }

        public Exception? Throws { get; set; }

        /// <summary>Answered to the next beat, then cleared. Models a server that throttles once.</summary>
        public FirstRunHeartbeatOutcome? Next { get; set; }

        /// <summary>Answered to every beat. Models a route that is simply not there.</summary>
        public FirstRunHeartbeatOutcome? Always { get; set; }

        /// <summary>Answered to beats in the order they were issued. Taken at call time, so pairing it
        /// with <see cref="HoldAnswers"/> makes a drain deterministic — which is what lets a test pin
        /// whose verdict survives one.</summary>
        public Queue<FirstRunHeartbeatOutcome>? Sequence { get; init; }

        /// <summary>Never completes, so the beat stays outstanding until the test ends.</summary>
        public bool BlockForever { get; init; }

        /// <summary>Held beats, released together by <see cref="ReleaseHeld"/>. Models an answer that
        /// arrives later than the interval that asked for it — the case carrying the verdict across
        /// ticks exists for, and one no synchronous fake can produce.</summary>
        public bool HoldAnswers { get; init; }

        /// <summary>Beat numbers (1-based) to hold until <see cref="ReleaseHeld"/>, so a test can have
        /// some answer at once and the rest land together. Models a server whose answers arrive out of
        /// the order they were asked for.</summary>
        public HashSet<int>? Hold { get; init; }

        readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHeld() => _held.TrySetResult();

        public List<CancellationToken> Tokens { get; } = [];

        public void Release() => _gate.TrySetResult();

        public async Task<FirstRunHeartbeatOutcome> HeartbeatAsync(
                string serverUrl, string flowId, CancellationToken ct) {
            var number = Interlocked.Increment(ref _beats);

            lock (Tokens) Tokens.Add(ct);

            FirstRunHeartbeatOutcome? sequenced = null;

            if (Sequence is { } queue)
                lock (queue)
                    if (queue.Count > 0)
                        sequenced = queue.Dequeue();

            if (BlockForever) await Task.Delay(Timeout.Infinite, ct);
            if (HoldAnswers) await _held.Task;
            if (Hold?.Contains(number) is true) await _held.Task;
            if (Block) await _gate.Task;
            if (Throws is { } boom) throw boom;

            Interlocked.Increment(ref _answered);

            if (sequenced is { } answer) return answer;

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

    /// <summary>
    /// As <see cref="ReachesAsync"/>, except the fake clock may only move <paramref name="budget"/>
    /// forward. Polling continues on the real deadline afterwards, so a starved runner reads as slow
    /// rather than as a shorter window than the test asked for.
    /// </summary>
    static async Task<bool> ReachesWithinAsync(
            FakeChannel channel, FakeTimeProvider clock, int target, TimeSpan budget) {
        var until    = DateTime.UtcNow + Patience;
        var deadline = clock.GetUtcNow() + budget;

        while (DateTime.UtcNow < until) {
            if (channel.Beats >= target) return true;

            if (clock.GetUtcNow() < deadline) clock.Advance(Beat);

            await Task.Delay(5);
        }

        return channel.Beats >= target;
    }

    /// <summary>Waits, on the real clock only, until <paramref name="answered"/> beats have returned an
    /// outcome. Moving the fake clock before that spends a tick on an empty drain.</summary>
    static async Task<bool> AnsweredAsync(FakeChannel channel, int answered) {
        var until = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < until && channel.Answered < answered) await Task.Delay(5);

        return channel.Answered >= answered;
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
    /// A hung beat is never cancelled, and the ones behind it keep the cadence up to the cap.
    ///
    /// <para><b>The no-cancel half is the load-bearing one.</b> The beat rides the setup client, whose
    /// 401 handler rotates a single-use refresh token and then persists it, the rotation itself being
    /// uncancellable. A cancel landing between the two spends the credential server-side and never writes
    /// the replacement, logging the user out mid-setup — so the stop token is kept off the request
    /// entirely and the client's own timeout is what ends a beat.</para>
    ///
    /// <para>The cap is the other half: a wedged network must not silence the machine for a whole client
    /// timeout, and must not accumulate one open POST per interval on the very machine whose network is
    /// failing either.</para>
    /// </summary>
    [Test]
    public async Task A_hung_beat_is_never_cancelled_and_does_not_hold_the_whole_budget() {
        var channel = new FakeChannel { BlockForever = true };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        for (var i = 0; i < 10; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        // Bounded, not stopped: a hung request must not silence the machine, and must not accumulate one
        // open POST per tick either.
        await Assert.That(channel.Beats).IsEqualTo(3)
                    .Because("beats are capped in flight, and a hung one must not hold the whole budget");

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
    /// A route this server does not have answers the same way every time, and beating on is hundreds of
    /// authenticated no-ops per run — enough to trip the very limiter the throttle handling exists to
    /// keep clear for the poll. So the beat backs off it, and probes again afterwards rather than giving
    /// up: a rolling deploy answers this way for minutes and then starts working.
    /// </summary>
    [Test]
    [Arguments(404)]
    [Arguments(405)]
    public async Task A_route_that_is_never_there_pauses_the_beat(int status) {
        var channel = new FakeChannel { Always = new FirstRunHeartbeatOutcome(status) };
        var clock   = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        for (var i = 0; i < 20; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        // Exactly the threshold: a lower number would pass here too, so an inequality would not pin the
        // constant the test is named for. Four, not three — the threshold sits above the in-flight cap so
        // that one round of simultaneous refusals cannot fill it.
        await Assert.That(channel.Beats).IsEqualTo(4)
                    .Because("the beat went on posting to a route the server does not have");

        // A pause, not an ending — the poll rides out a rolling deploy on the same client, so a beat that
        // stopped for good would have the browser infer a death from a machine still talking to it.
        clock.Advance(TimeSpan.FromMinutes(3));

        await Assert.That(await ReachesAsync(channel, clock, 5)).IsTrue()
                    .Because("the beat never probed the route again after backing off");
    }

    /// <summary>
    /// The reason a verdict is carried across ticks rather than dropped: a throttling server is exactly
    /// the one whose answer outruns the interval that asked for it. Every other fake here completes
    /// synchronously, so without this the behaviour this loop is built around is unpinned.
    /// </summary>
    [Test]
    public async Task A_verdict_that_arrives_late_is_still_acted_on() {
        var channel = new FakeChannel {
            HoldAnswers = true, Always = new FirstRunHeartbeatOutcome(429, TimeSpan.FromSeconds(90))
        };

        var clock = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        // Nothing has answered yet, so the loop fills its lane and stops.
        await ReachesAsync(channel, clock, MaxInFlightForTest);

        var issued = channel.Beats;

        channel.ReleaseHeld();

        // The throttle those answers carry has to take effect even though every one of them arrived
        // later than the tick that asked for it.
        for (var i = 0; i < 10; i++) {
            clock.Advance(Beat);

            await Task.Delay(5);
        }

        await Assert.That(channel.Beats).IsEqualTo(issued)
                    .Because("a throttle answered late was dropped, so the beat kept posting through it");
    }

    /// <summary>
    /// A drain can hold several answers, and each is the server's word at a different moment. One that
    /// throttles hard and then recovers sends the shorter delay last, so ending the drain on the oldest
    /// would obey an instruction the server had already withdrawn — two minutes of silence on a route
    /// that is answering.
    /// </summary>
    [Test]
    public async Task The_newest_answer_in_a_drain_is_the_one_that_stands() {
        var channel = new FakeChannel {
            HoldAnswers = true,
            Sequence = new([
                new FirstRunHeartbeatOutcome(429, TimeSpan.FromSeconds(120)),
                new FirstRunHeartbeatOutcome(429, TimeSpan.FromSeconds(120)),
                new FirstRunHeartbeatOutcome(429, TimeSpan.FromSeconds(1))
            ])
        };

        var clock = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        // Nothing answers until the lane is full, so all three land in one drain.
        await Assert.That(await ReachesAsync(channel, clock, MaxInFlightForTest)).IsTrue()
                    .Because("the drain this test is about never formed");

        channel.ReleaseHeld();

        await Assert.That(await AnsweredAsync(channel, MaxInFlightForTest)).IsTrue()
                    .Because("the released answers never arrived, so nothing was there to drain");

        // Well inside the 120s the two stale answers asked for, so obeying either of them fails here.
        await Assert.That(await ReachesWithinAsync(channel, clock, MaxInFlightForTest + 1,
                                                   TimeSpan.FromSeconds(30))).IsTrue()
                    .Because("a superseded Retry-After outlasted the one that replaced it");
    }

    /// <summary>
    /// A route that comes back has to be able to lift the pause its own refusals bought. A rolling
    /// deploy is the concrete case: drained pods refuse and new ones answer, and the two land in the
    /// same drain — so a window pushed out by the refusals would silence the machine for two minutes
    /// starting at the moment it recovered, on a client whose poll is succeeding throughout.
    /// </summary>
    [Test]
    public async Task An_answer_after_the_refusals_lifts_the_pause_they_would_have_bought() {
        var channel = new FakeChannel {
            Hold = [3, 4, 5],
            Sequence = new([
                new FirstRunHeartbeatOutcome(404),
                new FirstRunHeartbeatOutcome(404),
                new FirstRunHeartbeatOutcome(404),
                new FirstRunHeartbeatOutcome(404),
                new FirstRunHeartbeatOutcome(204)
            ])
        };

        var clock = new FakeTimeProvider();

        using var beat = FirstRunHeartbeat.Start(channel, Server, Flow, clock, Beat);

        // Two refusals answered on their own, then the lane fills with three that are still held —
        // enough that the drain crosses the threshold on its way to the answer that clears it.
        await Assert.That(await ReachesAsync(channel, clock, 2 + MaxInFlightForTest)).IsTrue()
                    .Because("the drain this test is about never formed");

        channel.ReleaseHeld();

        await Assert.That(await AnsweredAsync(channel, 2 + MaxInFlightForTest)).IsTrue()
                    .Because("the released answers never arrived, so nothing was there to drain");

        await Assert.That(await ReachesWithinAsync(channel, clock, 2 + MaxInFlightForTest + 1,
                                                   TimeSpan.FromSeconds(30))).IsTrue()
                    .Because("a route answering again was still held silent by the refusals ahead of it");
    }

    /// <summary>Mirrors the loop's own cap. Named here rather than read from the class, so a change to the
    /// constant shows up as a failing test rather than a silently re-scoped one.</summary>
    const int MaxInFlightForTest = 3;

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
