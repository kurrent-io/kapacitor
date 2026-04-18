using System.Text.Json;

namespace kapacitor.Tests.Unit;

public class ClaudeCliRunnerTests {
    [Test]
    public async Task ParseResponse_ValidJsonWithAllFields_ParsesCorrectly() {
        const string json = """
                            {
                                "result": "Hello, world!",
                                "total_cost_usd": 0.0042,
                                "modelUsage": {
                                    "claude-haiku-3": {
                                        "inputTokens": 100,
                                        "outputTokens": 50,
                                        "cacheReadInputTokens": 30,
                                        "cacheCreationInputTokens": 10
                                    }
                                }
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsEqualTo("Hello, world!");
        await Assert.That(result.Model).IsEqualTo("claude-haiku-3");
        await Assert.That(result.InputTokens).IsEqualTo(100);
        await Assert.That(result.OutputTokens).IsEqualTo(50);
        await Assert.That(result.CacheReadTokens).IsEqualTo(30);
        await Assert.That(result.CacheWriteTokens).IsEqualTo(10);
        await Assert.That(result.CostUsd).IsEqualTo(0.0042);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ParseResponse_JsonWithEmptyOrWhitespaceResult_ReturnsNull(string resultValue) {
        var json = $$"""
                     {
                         "result": "{{resultValue}}",
                         "total_cost_usd": 0.001
                     }
                     """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseResponse_PlainText_FallsBackToTextResult() {
        const string plainText = "This is not JSON, just plain text.";

        var result = ClaudeCliRunner.ParseResponse(plainText);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsEqualTo(plainText);
        await Assert.That(result.Model).IsNull();
        await Assert.That(result.InputTokens).IsEqualTo(0);
        await Assert.That(result.OutputTokens).IsEqualTo(0);
        await Assert.That(result.CacheReadTokens).IsEqualTo(0);
        await Assert.That(result.CacheWriteTokens).IsEqualTo(0);
        await Assert.That(result.CostUsd).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\n")]
    public async Task ParseResponse_EmptyOrWhitespaceString_ReturnsNull(string input) {
        var result = ClaudeCliRunner.ParseResponse(input);

        await Assert.That(result).IsNull();
    }

    // DEV-1476: when --json-schema is active, the CLI fulfils the reply via
    // the StructuredOutput tool, leaves `result` empty, and puts the matched
    // object under the top-level `structured_output` field. The re-serialised
    // text must be surfaced as Result so downstream verdict/retrospective
    // parsers keep working unchanged.
    [Test]
    public async Task ParseResponse_StructuredOutputObject_IsReserialisedAsResult() {
        const string json = """
                            {
                                "result": "",
                                "structured_output": {"score": 5, "verdict": "pass"},
                                "total_cost_usd": 0.002,
                                "modelUsage": {
                                    "claude-haiku-4-5": {"inputTokens": 10, "outputTokens": 3}
                                }
                            }
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        // GetRawText preserves the source whitespace — what matters is the
        // result round-trips back to the same object, which is what
        // downstream ParseVerdict/ParseRetrospective do.
        using var doc = JsonDocument.Parse(result!.Result);
        await Assert.That(doc.RootElement.GetProperty("score").GetInt32()).IsEqualTo(5);
        await Assert.That(doc.RootElement.GetProperty("verdict").GetString()).IsEqualTo("pass");
        await Assert.That(result.Model).IsEqualTo("claude-haiku-4-5");
        await Assert.That(result.InputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task ParseResponse_StructuredOutputWinsOverEmptyResult() {
        // Belt-and-braces: even without `result` entirely, structured_output
        // alone is enough. (Real CLI replies always include `result` but
        // this guards against future format tweaks.)
        const string json = """
                            {"structured_output": {"overall": "clean run"}}
                            """;

        var result = ClaudeCliRunner.ParseResponse(json);

        await Assert.That(result).IsNotNull();
        using var doc = JsonDocument.Parse(result!.Result);
        await Assert.That(doc.RootElement.GetProperty("overall").GetString()).IsEqualTo("clean run");
    }
}
