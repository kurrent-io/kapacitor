namespace kapacitor.Tests.Unit;

public class SecretRedactorTests {
    [Test]
    public async Task RedactsLine_PemPrivateKey_InToolResult() {
        var line = """
            {"type":"progress","data":{"message":{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy5AoG5cwSxzXf\n-----END RSA PRIVATE KEY-----","is_error":false}]}}},"uuid":"abc123","timestamp":"2026-01-01T00:00:00Z"}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn");
        await Assert.That(result).Contains("[REDACTED]");
        await Assert.That(result).Contains("toolu_1"); // structure preserved
    }

    [Test]
    public async Task RedactsLine_PemPrivateKey_InToolResult_DirectFormat() {
        // Direct format: root type=user, no progress wrapper
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_2","type":"tool_result","content":"-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy5AoG5cwSxzXf\n-----END RSA PRIVATE KEY-----","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn");
        await Assert.That(result).Contains("[REDACTED]");
        await Assert.That(result).Contains("toolu_2"); // structure preserved
    }

    [Test]
    public async Task PassesThrough_NonToolResult_Unchanged() {
        var line = """{"type":"progress","data":{"message":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}},"uuid":"abc","timestamp":"2026-01-01T00:00:00Z"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task PassesThrough_MalformedJson_Unchanged() {
        var line = "not json at all";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    [Arguments("ghp_ABCDEFghijklmnop1234567890abcdef12345678", "GitHub PAT")]
    [Arguments("gho_ABCDEFghijklmnop1234567890ab", "GitHub OAuth")]
    [Arguments("ghs_ABCDEFghijklmnop1234567890ab", "GitHub App")]
    [Arguments("github_pat_ABCDEF1234567890abcdef", "GitHub fine-grained PAT")]
    [Arguments("cfat_ABCDEFghijklmnop1234567890ab", "Cloudflare")]
    [Arguments("sk-proj-ABCDEFghijklmnop1234567890ab", "OpenAI")]
    [Arguments("sk_live_ABCDEFghijklmnop1234567890ab", "Stripe live")]
    [Arguments("sk_test_ABCDEFghijklmnop1234567890ab", "Stripe test")]
    [Arguments("xoxb-1234-5678-abcdef", "Slack bot")]
    [Arguments("xoxp-1234-5678-abcdef", "Slack user")]
    [Arguments("pypi-ABCDEFghijklmnop1234567890ab", "PyPI")]
    [Arguments("npm_ABCDEFghijklmnop1234567890ab", "npm")]
    [Arguments("glpat-ABCDEFghijklmnop12345", "GitLab PAT")]
    public async Task RedactsLine_VendorToken_InToolResult(string token, string _description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"The token is {{{token}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(token);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AwsAccessKey_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).Contains("[REDACTED]");
    }
}
