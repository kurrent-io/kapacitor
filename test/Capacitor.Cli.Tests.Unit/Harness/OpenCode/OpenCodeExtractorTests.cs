using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

/// <summary>
/// the watcher must read OpenCode's <c>{info,parts}</c> shape for
/// title/event extraction (info.role + text parts) rather than falling through to the Claude
/// <c>type:"user"|"assistant"</c> shape — otherwise OpenCode sessions get no titles. Mirrors
/// the server normalizer's text rules (joined non-hidden text parts; synthetic/ignored skipped).
/// </summary>
public class OpenCodeExtractorTests {
    const string UserLine =
        """{"info":{"role":"user","id":"msg_u"},"parts":[{"type":"text","text":"summarize the files"}]}""";
    const string AssistantLine =
        """{"info":{"role":"assistant","id":"msg_a","finish":"stop"},"parts":[{"type":"reasoning","text":"thinking"},{"type":"text","text":"here is the summary"}]}""";

    [Test]
    public async Task TryExtractUserText_OpenCode_JoinsTextParts_OnlyUserRole() {
        var two = """{"info":{"role":"user"},"parts":[{"type":"text","text":"first"},{"type":"text","text":"second"}]}""";
        await Assert.That(WatchCommand.TryExtractUserText(two, "opencode")).IsEqualTo("first\nsecond");
        await Assert.That(WatchCommand.TryExtractUserText(UserLine, "opencode")).IsEqualTo("summarize the files");
        await Assert.That(WatchCommand.TryExtractUserText(AssistantLine, "opencode")).IsNull();
    }

    [Test]
    public async Task TryExtractAssistantText_OpenCode_ReturnsText_SkipsReasoning() {
        await Assert.That(WatchCommand.TryExtractAssistantText(AssistantLine, "opencode")).IsEqualTo("here is the summary");
        await Assert.That(WatchCommand.TryExtractAssistantText(UserLine, "opencode")).IsNull();
    }

    [Test]
    public async Task Extractors_SkipSyntheticAndIgnoredParts() {
        var synthetic = """{"info":{"role":"user"},"parts":[{"type":"text","text":"injected","synthetic":true},{"type":"text","text":"real prompt"}]}""";
        await Assert.That(WatchCommand.TryExtractUserText(synthetic, "opencode")).IsEqualTo("real prompt");

        var ignored = """{"info":{"role":"user"},"parts":[{"type":"text","text":"elided","ignored":true}]}""";
        await Assert.That(WatchCommand.TryExtractUserText(ignored, "opencode")).IsNull();
    }

    [Test]
    public async Task IsEvent_OpenCode_CountsTextTurns_NotToolOnlyOrOtherRoles() {
        await Assert.That(WatchCommand.IsEvent(UserLine, "opencode")).IsTrue();
        await Assert.That(WatchCommand.IsEvent(AssistantLine, "opencode")).IsTrue();

        // A tool-only assistant turn has no titleable text → must not count toward the threshold.
        var toolOnly = """{"info":{"role":"assistant"},"parts":[{"type":"tool","tool":"read","callID":"c","state":{"status":"completed"}}]}""";
        await Assert.That(WatchCommand.IsEvent(toolOnly, "opencode")).IsFalse();

        // Non user/assistant roles never count.
        var system = """{"info":{"role":"system"},"parts":[{"type":"text","text":"x"}]}""";
        await Assert.That(WatchCommand.IsEvent(system, "opencode")).IsFalse();
    }
}
