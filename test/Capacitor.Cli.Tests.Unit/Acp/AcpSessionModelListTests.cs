using System.Text.Json;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The two shapes an ACP agent publishes its selectable models in. Before this extraction existed the
/// resolver read only <c>models.availableModels</c>, so OpenCode — which publishes
/// <c>configOptions</c> and no <c>models</c> object at all — resolved nothing on every request and a
/// caller-requested model was silently discarded.
/// </summary>
public class AcpSessionModelListTests {
    static JsonElement Result(string json) => JsonDocument.Parse(json).RootElement;

    // The real opencode acp 1.18.9 session/new shape, trimmed: no `models`, a `model` config option
    // among siblings, provider/model values and display labels.
    const string OpenCodeResult = """
        { "sessionId": "ses_1",
          "configOptions": [
            { "id": "model", "name": "Model", "category": "model", "type": "select",
              "currentValue": "opencode/big-pickle",
              "options": [ { "value": "opencode/big-pickle", "name": "OpenCode Zen/Big Pickle" },
                           { "value": "opencode/deepseek-v4-flash-free", "name": "OpenCode Zen/DeepSeek V4 Flash Free" } ] },
            { "id": "mode", "name": "Session Mode", "type": "select", "currentValue": "build",
              "options": [ { "value": "build", "name": "build" } ] } ] }
        """;

    // The Cursor/Kiro shape.
    const string ModelsResult = """
        { "sessionId": "ses_2",
          "models": { "currentModelId": "claude-sonnet-4-5[thinking=true]",
                      "availableModels": [ { "modelId": "claude-sonnet-4-5[thinking=true]", "name": "claude-sonnet-4-5" },
                                           { "modelId": "claude-haiku-4-5", "name": "claude-haiku-4-5" } ] } }
        """;

    [Test]
    public async Task ConfigOptions_YieldTheModelOptionsAsAvailableModels() {
        var models = AcpSessionModelList.Extract(Result(OpenCodeResult));

        await Assert.That(models.Select(m => m.ModelId))
            .IsEquivalentTo(new[] { "opencode/big-pickle", "opencode/deepseek-v4-flash-free" });
        await Assert.That(models[1].Name).IsEqualTo("OpenCode Zen/DeepSeek V4 Flash Free");
    }

    /// <summary>
    /// The `mode` option must not be mistaken for the model list — it has the SAME structure
    /// (`currentValue` + `options[{value,name}]`), so reading "the first configOption" instead of the
    /// one whose id is `model` would silently resolve session modes as if they were models.
    ///
    /// <para><b>The sibling is deliberately FIRST here.</b> Asserting this against
    /// <see cref="OpenCodeResult"/>, where `model` happens to lead the array, does not discriminate:
    /// dropping the id check entirely still returns the model entry, and a mutation run confirmed
    /// exactly that survivor. Nothing in the payload promises an order, so the order this test uses
    /// has to be the one that fails when the check is gone.</para>
    /// </summary>
    [Test]
    public async Task ConfigOptions_IgnoreSiblingOptionsThatAreNotTheModel() {
        var siblingFirst = Result("""
            { "sessionId": "ses_5",
              "configOptions": [
                { "id": "mode", "currentValue": "build",
                  "options": [ { "value": "build", "name": "build" }, { "value": "plan", "name": "plan" } ] },
                { "id": "effort", "currentValue": "low",
                  "options": [ { "value": "low", "name": "Low" } ] },
                { "id": "model", "currentValue": "opencode/big-pickle",
                  "options": [ { "value": "opencode/big-pickle", "name": "OpenCode Zen/Big Pickle" } ] } ] }
            """);

        var models = AcpSessionModelList.Extract(siblingFirst);

        await Assert.That(models.Select(m => m.ModelId)).IsEquivalentTo(new[] { "opencode/big-pickle" });
    }

    [Test]
    public async Task ModelsObject_StillResolvesUnchanged() {
        var models = AcpSessionModelList.Extract(Result(ModelsResult));

        await Assert.That(models.Select(m => m.ModelId))
            .IsEquivalentTo(new[] { "claude-sonnet-4-5[thinking=true]", "claude-haiku-4-5" });
    }

    /// <summary>
    /// Precedence, pinned. A vendor carrying both shapes must be read through the standardized one —
    /// the shape its wire selector was designed against — rather than having a mirror reinterpreted as
    /// the authority.
    /// </summary>
    [Test]
    public async Task ModelsObject_WinsOverConfigOptions() {
        var both = Result("""
            { "sessionId": "ses_3",
              "models": { "availableModels": [ { "modelId": "from-models", "name": "m" } ] },
              "configOptions": [ { "id": "model", "currentValue": "from-config",
                                   "options": [ { "value": "from-config", "name": "c" } ] } ] }
            """);

        var models = AcpSessionModelList.Extract(both);

        await Assert.That(models.Select(m => m.ModelId)).IsEquivalentTo(new[] { "from-models" });
    }

    /// <summary>
    /// An EMPTY models object must not shadow a populated configOptions list. `models` winning is
    /// about which shape is authoritative when both carry a list, not about a present-but-empty
    /// property blocking the fallback — a vendor that answers `"models": {}` would otherwise lose
    /// model selection entirely.
    /// </summary>
    [Test]
    public async Task AnEmptyModelsObject_FallsThroughToConfigOptions() {
        var element = Result("""
            { "sessionId": "ses_4",
              "models": { "availableModels": [] },
              "configOptions": [ { "id": "model", "currentValue": "from-config",
                                   "options": [ { "value": "from-config", "name": "c" } ] } ] }
            """);

        await Assert.That(AcpSessionModelList.Extract(element).Select(m => m.ModelId))
            .IsEquivalentTo(new[] { "from-config" });
    }

    [Test]
    [Arguments("""{ "sessionId": "s" }""")]
    [Arguments("""{ "sessionId": "s", "configOptions": [] }""")]
    [Arguments("""{ "sessionId": "s", "configOptions": [ { "id": "mode", "options": [] } ] }""")]
    [Arguments("""{ "sessionId": "s", "configOptions": "not-an-array" }""")]
    [Arguments("""{ "sessionId": "s", "models": "not-an-object" }""")]
    [Arguments("""[ "not", "an", "object" ]""")]
    public async Task NoPublishedList_YieldsEmptyRatherThanThrowing(string json) {
        await Assert.That(AcpSessionModelList.Extract(Result(json))).IsEmpty();
    }

    /// <summary>
    /// One unreadable sibling must not hide a readable `model` entry: model selection is best-effort,
    /// and the failure mode to avoid is a malformed option elsewhere in the array silently costing a
    /// launch its requested model.
    /// </summary>
    [Test]
    public async Task AMalformedSibling_DoesNotHideTheModelOption() {
        var element = Result("""
            { "sessionId": "s",
              "configOptions": [ { "id": "mode", "options": "not-an-array" },
                                 { "id": "model", "options": [ { "value": "good", "name": "g" } ] } ] }
            """);

        await Assert.That(AcpSessionModelList.Extract(element).Select(m => m.ModelId))
            .IsEquivalentTo(new[] { "good" });
    }

    /// <summary>
    /// A <c>models</c> entry with no <c>modelId</c> must be dropped, not passed on.
    ///
    /// <para><c>AvailableModelDto.ModelId</c> is non-nullable in C# but nothing stops an agent omitting
    /// it, and <see cref="AcpModelResolver.Resolve"/>'s prefix arm then calls <c>StartsWith</c> on null
    /// and throws past a caller whose only guard is <c>JsonException</c> — turning a malformed vendor
    /// response into a failed LAUNCH, for a feature that is never supposed to be a launch precondition.
    /// The <c>configOptions</c> path always filtered; this one did not, which is what review caught.</para>
    /// </summary>
    [Test]
    public async Task AModelsEntryWithNoModelId_IsDroppedRatherThanCrashingTheResolver() {
        var element = Result("""
            { "sessionId": "s",
              "models": { "availableModels": [ { "name": "no id here" },
                                               { "modelId": "   ", "name": "blank" },
                                               { "modelId": "good-id", "name": "fine" } ] } }
            """);

        var models = AcpSessionModelList.Extract(element);

        await Assert.That(models.Select(m => m.ModelId)).IsEquivalentTo(new[] { "good-id" });
        // The assertion that would have thrown before the filter.
        await Assert.That(AcpModelResolver.Resolve("good", models)).IsEqualTo("good-id");
        await Assert.That(AcpModelResolver.Resolve("nothing-matches", models)).IsNull();
    }

    /// <summary>
    /// End to end with the resolver, on the real shape: an exact id, a bare prefix, and a display
    /// label all reach the exact wire value `session/set_config_option` requires.
    /// </summary>
    [Test]
    [Arguments("opencode/deepseek-v4-flash-free", "opencode/deepseek-v4-flash-free")]
    [Arguments("opencode/deepseek", "opencode/deepseek-v4-flash-free")]
    [Arguments("DeepSeek V4 Flash Free", "opencode/deepseek-v4-flash-free")]
    [Arguments("opencode/big-pickle", "opencode/big-pickle")]
    [Arguments("no-such-model", null)]
    public async Task ResolvesAgainstTheOpenCodeList(string requested, string? expected) {
        var models = AcpSessionModelList.Extract(Result(OpenCodeResult));

        await Assert.That(AcpModelResolver.Resolve(requested, models)).IsEqualTo(expected);
    }
}
