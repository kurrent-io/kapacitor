using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptDiscoveryTests {
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [Test]
    public async Task A_winner_on_a_later_tick_is_handed_over_once() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        var calls = 0;
        var found = new List<(string, string)>();

        var run = discovery.RunAsync(
            _ => ++calls >= 3 ? ("sid", "/t.jsonl") : null,
            w => { found.Add(w); return Task.CompletedTask; },
            CancellationToken.None);

        time.Advance(Interval);
        time.Advance(Interval);
        await Assert.That(await run).IsTrue();
        await Assert.That(found).IsEquivalentTo(new[] { ("sid", "/t.jsonl") });
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task The_ruled_out_set_persists_across_ticks() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        ISet<string>? first = null, second = null;

        var run = discovery.RunAsync(
            set => { if (first is null) { first = set; set.Add("x"); return null; } second = set; return ("sid", "/p"); },
            _ => Task.CompletedTask, CancellationToken.None);

        time.Advance(Interval);
        await run;
        await Assert.That(second).IsSameReferenceAs(first!);
        await Assert.That(second!.Contains("x")).IsTrue();
    }

    [Test]
    public async Task The_deadline_ends_the_poll_without_a_handover() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        var handed = false;

        var run = discovery.RunAsync(_ => null, _ => { handed = true; return Task.CompletedTask; }, CancellationToken.None);
        for (var elapsed = TimeSpan.Zero; elapsed <= Timeout; elapsed += Interval) time.Advance(Interval);

        await Assert.That(await run).IsFalse();
        await Assert.That(handed).IsFalse();
    }

    [Test]
    public async Task Cancellation_ends_the_poll_cleanly_without_a_final_locate() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var run = discovery.RunAsync(_ => { calls++; return null; }, _ => Task.CompletedTask, cts.Token);
        cts.Cancel();

        await Assert.That(await run).IsFalse();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_fault_in_onFound_propagates_and_is_not_reported_as_not_found() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);

        var run = discovery.RunAsync(
            _ => ("sid", "/t.jsonl"),
            _ => throw new InvalidOperationException("report failed"),
            CancellationToken.None);

        await Assert.That(async () => await run).Throws<InvalidOperationException>();
    }
}
