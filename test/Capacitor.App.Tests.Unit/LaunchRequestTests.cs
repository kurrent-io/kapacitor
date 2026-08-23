using System.Text.Json;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LaunchRequestTests {
    // Source-generated context, not the reflection overload: this assembly is AOT-published.
    static JsonElement Payload(LaunchRequest request) =>
        JsonSerializer.SerializeToElement(
            LaunchPayload.For(request), LaunchJsonContext.Default.LaunchAgentRequestV2Payload);

    [Test]
    public async Task Model_is_the_empty_string_sentinel_not_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "cursor", "go"));

        // Server contract: "" means "use the vendor default"; null is not a legal value.
        await Assert.That(json.GetProperty("model").GetString()).IsEqualTo("");
    }

    [Test]
    public async Task Vendor_is_always_sent_explicitly() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "gemini", null));

        // A null vendor normalizes to Claude server-side — never rely on that.
        await Assert.That(json.GetProperty("vendor").GetString()).IsEqualTo("gemini");
    }

    [Test]
    public async Task Prompt_and_repo_path_are_carried_verbatim() {
        var json = Payload(new LaunchRequest("kcap-dev", "/home/a/kcap-cli", "claude", "Fix the flaky test"));

        await Assert.That(json.GetProperty("prompt").GetString()).IsEqualTo("Fix the flaky test");
        await Assert.That(json.GetProperty("repoPath").GetString()).IsEqualTo("/home/a/kcap-cli");
        await Assert.That(json.GetProperty("daemonName").GetString()).IsEqualTo("kcap-dev");
    }

    [Test]
    public async Task Blank_prompt_is_sent_as_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "   "));
        await Assert.That(json.GetProperty("prompt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
