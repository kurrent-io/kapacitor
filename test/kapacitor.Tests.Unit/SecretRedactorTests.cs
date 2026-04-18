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
    [Arguments("sk_live_00000000000FAKEFAKEFAKE00", "Stripe live")]
    [Arguments("sk_test_00000000000FAKEFAKEFAKE00", "Stripe test")]
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

    [Test]
    [Arguments("ASIASUOU4HYNTGJIZ23T", "STS temporary access key")]
    [Arguments("AROASUOU4HYN3THVQBHO5", "IAM role unique ID")]
    [Arguments("AIDAJQABLZS4A3QDU576Q", "IAM user unique ID")]
    [Arguments("AIPAIFHHFHABCDEF12345", "EC2 instance profile ID")]
    [Arguments("AGPAI23HXD2XYZ123ABCD", "IAM group ID")]
    public async Task RedactsLine_AwsUniqueId_InToolResult(string id, string _description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"id is {{{id}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(id);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_SecretAccessKey_InTruncatedToolOutput() {
        // Reproduces a real leak: a shell script prints only the first N chars of a JSON
        // credentials blob, so the SecretAccessKey value has no closing quote.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"First 100 chars: {\n    \"AccessKeyId\": \"ASIASUOU4HYNTGJIZ23T\",\n    \"SecretAccessKey\": \"0X+FPiUxddhI7babryQv4l5JQ37Smuy","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("0X+FPiUxddhI7babryQv4l5JQ37Smuy");
        await Assert.That(result).DoesNotContain("ASIASUOU4HYNTGJIZ23T");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_JsonKeySecret_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{ \"client_secret\": \"a8f3b2c91d4e7f0123456789abcdef01\" }","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("a8f3b2c91d4e7f0123456789abcdef01");
        await Assert.That(result).Contains("client_secret");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_EnvVarToken_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"HETZNER_API_TOKEN=abc123def456ghi789jkl","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl");
        await Assert.That(result).Contains("HETZNER_API_TOKEN");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_YamlStyleSecret_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"api_key: sk_reallyLongSecretValue123456","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("sk_reallyLongSecretValue123456");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_ConnectionStringPassword_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Server=localhost;Database=mydb;Password=hunter2;User Id=sa","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("hunter2");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_CleanToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"total 40\ndrwxr-xr-x  8 user staff 256 Feb 11 22:54 .\n-rw-r--r--  1 user staff 611 Feb 10 22:33 Program.cs","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_SecretInToolUseResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"ok","is_error":false}]},"toolUseResult":{"content":"-----BEGIN EC PRIVATE KEY-----\nMHQCAQEEIBkg\n-----END EC PRIVATE KEY-----"}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("MHQCAQEEIBkg");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task PassesThrough_UserMessage_Unchanged() {
        var line = """{"type":"user","message":{"role":"user","content":"my secret password is hunter2"}}""";

        var result = SecretRedactor.RedactLine(line);

        // User messages with string content (not array with tool_result) are not scanned
        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task PassesThrough_ToolUse_Unchanged() {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/etc/secret.pem"}}]}}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_MultipleSecrets_InSameToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"GITHUB_TOKEN=ghp_abc123def456ghi789jkl012mno345pqrs678\nAWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE\nDB_PASSWORD=supersecretvalue123","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).DoesNotContain("supersecretvalue123");
    }

    [Test]
    public async Task PassesThrough_EmptyLine_Unchanged() {
        await Assert.That(SecretRedactor.RedactLine("")).IsEqualTo("");
    }

    [Test]
    public async Task PassesThrough_WhitespaceLine_Unchanged() {
        await Assert.That(SecretRedactor.RedactLine("   ")).IsEqualTo("   ");
    }

    [Test]
    public async Task PreservesValidJson_AfterEnvVarRedaction() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"HETZNER_API_TOKEN=abc123def456ghi789jkl","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        // Must still be valid JSON after redaction
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content");
        // is_error field must survive redaction
        await Assert.That(content[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task PreservesValidJson_AfterConnectionStringRedaction() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Server=localhost;Database=mydb;Password=hunter2;User Id=sa","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content");
        await Assert.That(content[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }
}
