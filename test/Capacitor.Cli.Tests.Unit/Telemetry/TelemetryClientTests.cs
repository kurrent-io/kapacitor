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
