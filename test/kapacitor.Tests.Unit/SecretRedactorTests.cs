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
}
