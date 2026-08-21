using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

public class CursorHookEventMapTests {
    [Test]
    [Arguments("sessionStart",        "session-start/cursor",         true)]
    [Arguments("sessionEnd",          "session-end/cursor",           true)]
    [Arguments("beforeSubmitPrompt",  "user-prompt/cursor",           true)]
    [Arguments("afterAgentThought",   "agent-thought/cursor",         true)]
    [Arguments("afterAgentResponse",  "agent-response/cursor",        false)]
    [Arguments("preToolUse",          "pre-tool-use/cursor",          false)]
    [Arguments("postToolUse",         "post-tool-use/cursor",         false)]
    [Arguments("postToolUseFailure",  "post-tool-use-failure/cursor", false)]
    public async Task TryResolve_known_events_map_correctly(string evt, string expectedSegment, bool expectedCanonical) {
        await Assert.That(CursorHookEventMap.TryResolve(evt, out var mapping)).IsTrue();
        await Assert.That(mapping.RouteSegment).IsEqualTo(expectedSegment);
        await Assert.That(mapping.SpoolOnFailure).IsEqualTo(expectedCanonical);
    }

    [Test]
    public async Task TryResolve_unknown_event_returns_false() {
        await Assert.That(CursorHookEventMap.TryResolve("madeUpEvent", out _)).IsFalse();
    }

    [Test]
    public async Task TryResolve_empty_or_null_event_returns_false() {
        await Assert.That(CursorHookEventMap.TryResolve("", out _)).IsFalse();
        await Assert.That(CursorHookEventMap.TryResolve(null!, out _)).IsFalse();
    }
}
