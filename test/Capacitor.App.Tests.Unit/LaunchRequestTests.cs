using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Models.Transcripts;

namespace Capacitor.App.Tests.Unit;

public class LaunchRequestTests {
    // The genuine on-wire options — same JsonSerializerOptions ServerLaunchClient hands
    // AddJsonProtocol, via the shared LaunchHubJson.Configure. Serializing through a bare
    // context (as this test used to) proves nothing about what SignalR actually sends: the
    // server applies snake_case to every hub payload, and camelCase keys here would have
    // bound null server-side while every test stayed green.
    static readonly JsonSerializerOptions Options = BuildOptions();

    static JsonSerializerOptions BuildOptions() {
        var options = new JsonSerializerOptions();
        LaunchHubJson.Configure(options);
        return options;
    }

    static JsonElement Payload(LaunchRequest request) =>
        JsonSerializer.SerializeToElement(LaunchPayload.For(request), Options);

    [Test]
    public async Task Model_is_the_empty_string_sentinel_not_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "cursor", "go"));

        // Server contract: "" means "use the vendor default"; null is not a legal value.
        await Assert.That(json.GetProperty("model").GetString()).IsEqualTo("");
    }

    [Test]
    public async Task Model_and_effort_are_carried_when_chosen() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go", Model: " claude-fable-5 ", Effort: "high"));

        await Assert.That(json.GetProperty("model").GetString()).IsEqualTo("claude-fable-5"); // trimmed
        await Assert.That(json.GetProperty("effort").GetString()).IsEqualTo("high");
    }

    [Test]
    public async Task Blank_effort_is_sent_as_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go", Effort: "  "));
        await Assert.That(json.GetProperty("effort").IsNull).IsTrue();
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
        await Assert.That(json.GetProperty("repo_path").GetString()).IsEqualTo("/home/a/kcap-cli");
        await Assert.That(json.GetProperty("daemon_name").GetString()).IsEqualTo("kcap-dev");
    }

    [Test]
    public async Task Blank_prompt_is_sent_as_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "   "));
        await Assert.That(json.GetProperty("prompt").IsNull).IsTrue();
    }

    [Test]
    public async Task Permission_mode_is_carried_when_chosen() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go", PermissionMode: "acceptEdits"));
        await Assert.That(json.GetProperty("permission_mode").GetString()).IsEqualTo("acceptEdits");
    }

    /// An absent mode adds no key: the payload an older server receives is exactly the one it
    /// received before the field existed.
    [Test]
    public async Task Blank_permission_mode_is_omitted() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go", PermissionMode: "  "));
        await Assert.That(json.TryGetProperty("permission_mode", out _)).IsFalse();
    }

    [Test]
    public async Task Wire_keys_are_exactly_the_twelve_hub_fields_when_no_mode_is_chosen() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go"));
        var keys = json.EnumerateObject().Select(p => p.Name).ToArray();

        // Pins the complete RequestLaunchAgentV2 key set so a member added or renamed on
        // either side (this payload or the hub argument) fails loudly instead of silently
        // binding null.
        await Assert.That(keys).IsEquivalentTo([
            "daemon_name", "prompt", "model", "effort", "repo_path", "tools",
            "attachment_ids", "visibility", "grants", "vendor", "codex_posture", "acp_permission_preset"
        ]);
    }

    [Test]
    public async Task A_chosen_mode_adds_the_permission_mode_key() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "go", PermissionMode: "auto"));
        var keys = json.EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(keys).IsEquivalentTo([
            "daemon_name", "prompt", "model", "effort", "repo_path", "tools",
            "attachment_ids", "visibility", "grants", "vendor", "codex_posture", "acp_permission_preset",
            "permission_mode"
        ]);
    }
}
