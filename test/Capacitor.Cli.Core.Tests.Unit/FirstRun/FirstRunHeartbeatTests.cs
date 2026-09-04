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

        public void Release() => _gate.TrySetResult();

        public async Task<FirstRunHeartbeatOutcome> HeartbeatAsync(
                string serverUrl, string flowId, CancellationToken ct) {
            Interlocked.Increment(ref _beats);

            if (Block) await _gate.Task;
            if (Throws is { } boom) throw boom;

            return new(204);
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

        await ReachesAsync(channel, clock, 2);
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
}
