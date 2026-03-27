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
}
