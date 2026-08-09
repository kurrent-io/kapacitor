using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetryClientTests {
    sealed class StubHandler(HttpStatusCode status, Exception? throws = null) : HttpMessageHandler {
        public int    Calls    { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws is not null) throw throws;

            return new HttpResponseMessage(status);
        }
    }

    static string NewSpoolPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-client-{Guid.NewGuid():N}", "spool.jsonl");

    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    static TelemetryClient Client(StubHandler handler, out TelemetrySpool spool) {
        spool = new TelemetrySpool(NewSpoolPath());
        return new TelemetryClient(handler, spool, "phc_test", "https://phog.example");
    }

    // Deterministic stand-in for real elapsed time: FlushAsync calls GetTimestamp() once at
    // entry and once more (via GetElapsedTime) after the drain/serialize phase, so two calls
    // apart by a fixed step simulates "that phase alone took `step`" without racing real
    // wall-clock timing or needing an artificially slow spool.
    sealed class FakeTimeProvider(TimeSpan step) : TimeProvider {
        long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() {
            var t = _timestamp;
            _timestamp += step.Ticks;
            return t;
        }
    }

    [Test]
    public async Task Flush_with_empty_queue_makes_no_request() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Flush_posts_queued_events() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.LastBody!.Contains("cli_command")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("device-1")).IsTrue();
    }

    [Test]
    public async Task Successful_flush_empties_the_queue() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_flush_spills_to_the_spool() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var client  = Client(handler, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Network_exception_spills_rather_than_propagating() {
        var handler = new StubHandler(HttpStatusCode.OK, new HttpRequestException("offline"));
        var client  = Client(handler, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Spooled_events_are_replayed_on_the_next_flush() {
        var failing = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var spool   = new TelemetrySpool(NewSpoolPath());

        var first = new TelemetryClient(failing, spool, "phc_test", "https://phog.example");
        first.Enqueue(Event("offline_event"));
        await first.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        var ok      = new StubHandler(HttpStatusCode.OK);
        var second  = new TelemetryClient(ok, spool, "phc_test", "https://phog.example");
        second.Enqueue(Event("fresh_event"));
        var flushed = await second.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(flushed).IsTrue();
        await Assert.That(ok.LastBody!.Contains("offline_event")).IsTrue();
        await Assert.That(ok.LastBody!.Contains("fresh_event")).IsTrue();
        // Ordering: spooled events (offline) come before queued events (fresh)
        await Assert.That(ok.LastBody!.IndexOf("offline_event") < ok.LastBody!.IndexOf("fresh_event")).IsTrue();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repeated_failures_do_not_duplicate_spooled_events() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var client  = Client(handler, out var spool);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    // Regression test for the finding that FlushAsync's budget only bounded the HTTP phase:
    // DrainAll (disk I/O) and PostHogPayload.Build (serializing the batch) ran unbounded before
    // the timer started, so a slow drain could still let a doomed HTTP request start and burn the
    // remaining wall-clock time anyway. With the fix, the drain/serialize phase (simulated here
    // as a deterministic 10-second "elapsed" via FakeTimeProvider) is charged against the budget
    // BEFORE the HTTP phase — a 1-second budget is already exhausted by then, so no request is
    // attempted at all. Against the old (unfixed) code this budget difference is invisible: the
    // full 1-second budget would still reach the HTTP phase, and the stub handler (which responds
    // instantly, no real network latency) would succeed well within it — ok=true, Calls=1 — so
    // this test fails on the old code and passes only once the remaining-budget check exists.
    [Test]
    public async Task Budget_already_exhausted_by_drain_and_build_spills_without_a_request() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var spool   = new TelemetrySpool(NewSpoolPath());
        var clock   = new FakeTimeProvider(TimeSpan.FromSeconds(10));
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", clock);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(1));

        await Assert.That(ok).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(0);
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    // Sanity check for the same fake clock: when the simulated drain/serialize phase leaves
    // budget to spare, the HTTP phase still proceeds normally and can succeed. Without this, a
    // bug that made the "exhausted" branch fire unconditionally would still pass the test above.
    [Test]
    public async Task Budget_with_time_remaining_after_drain_and_build_still_posts() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var spool   = new TelemetrySpool(NewSpoolPath());
        var clock   = new FakeTimeProvider(TimeSpan.FromMilliseconds(1));
        var client  = new TelemetryClient(handler, spool, "phc_test", "https://phog.example", clock);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Org_group_reaches_the_payload() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", "acme", TimeSpan.FromSeconds(2));

        await Assert.That(handler.LastBody!.Contains("organization")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("acme")).IsTrue();
    }
}
