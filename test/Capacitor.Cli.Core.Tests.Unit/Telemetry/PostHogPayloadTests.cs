using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class PostHogPayloadTests {
    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Batch_carries_token_and_events() {
        var json = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var root = Parse(json);

        await Assert.That(root["api_key"]!.GetValue<string>()).IsEqualTo("phc_test");
        await Assert.That(root["batch"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(root["batch"]![0]!["event"]!.GetValue<string>()).IsEqualTo("cli_command");
    }

    [Test]
    public async Task Every_event_carries_distinct_id_and_suppresses_geoip() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["distinct_id"]!.GetValue<string>()).IsEqualTo("device-1");
        await Assert.That(props["$ip"]).IsNull();
        await Assert.That(props.ContainsKey("$ip")).IsTrue();
        // $ip: null alone does not suppress PostHog's GeoIP enrichment (it falls back to the
        // connecting IP) — $geoip_disable: true is the documented switch for the enrichment itself.
        await Assert.That(props["$geoip_disable"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Org_group_and_property_are_attached_together() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: "acme");
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["$groups"]!["organization"]!.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(props["org"]!.GetValue<string>()).IsEqualTo("acme");
    }

    [Test]
    public async Task Org_group_and_property_are_both_absent_when_null() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props.ContainsKey("$groups")).IsFalse();
        await Assert.That(props.ContainsKey("org")).IsFalse();
    }

    [Test]
    public async Task Existing_properties_survive() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["source"]!.GetValue<string>()).IsEqualTo("cli");
    }

    [Test]
    public async Task Timestamp_is_round_trip_iso8601() {
        var json = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", null);
        var ts   = Parse(json)["batch"]![0]!["timestamp"]!.GetValue<string>();

        await Assert.That(DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture)).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    // The org group is only sound where the Helm chart guarantees Tenant__Name == slug.
    [Test]
    [Arguments("https://acme.kcap.ai", "acme")]
    [Arguments("https://acme.kcap.ai/", "acme")]
    [Arguments("https://ACME.kcap.ai", "acme")]
    public async Task Saas_urls_yield_the_slug(string url, string expected) {
        await Assert.That(PostHogPayload.OrgGroup(url)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://capacitor.internal.corp")]
    [Arguments("http://localhost:5000")]
    [Arguments("https://kcap.ai")]
    [Arguments("not a url")]
    [Arguments(null)]
    public async Task Non_saas_urls_yield_no_group(string? url) {
        await Assert.That(PostHogPayload.OrgGroup(url)).IsNull();
    }

    // Build must not graft payload fields onto the caller's event: Task 6 re-serialises spooled
    // events on retry, so in-place mutation would compound across attempts.
    // No nested-mutation counterpart: JsonNode enforces a single-parent invariant, so a shallow
    // copy of Properties is not constructible — dropping DeepClone() makes Build throw rather
    // than silently alias. The only reachable regression is Build writing into the caller's
    // object directly, which is what this test catches.
    [Test]
    public async Task Build_does_not_mutate_the_source_event() {
        var e = Event("cli_command");

        PostHogPayload.Build([e], "phc_test", "device-1", orgGroup: "acme");

        await Assert.That(e.Properties.Count).IsEqualTo(1);
        await Assert.That(e.Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(e.Properties.ContainsKey("distinct_id")).IsFalse();
        await Assert.That(e.Properties.ContainsKey("$ip")).IsFalse();
        await Assert.That(e.Properties.ContainsKey("$geoip_disable")).IsFalse();
        await Assert.That(e.Properties.ContainsKey("$groups")).IsFalse();
        await Assert.That(e.Properties.ContainsKey("org")).IsFalse();
    }
}
