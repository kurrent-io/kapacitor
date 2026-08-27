using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Tests.Unit;

public class SecretRedactorTests {
    [Test]
    public async Task RedactsLine_TruncatedPemPrivateKey_DoesNotHangOnBacktracking() {
        // Regression: a tool result containing `-----BEGIN RSA PRIVATE KEY-----` followed by many
        // `\n`-escaped key body lines, then truncated WITHOUT a matching `-----END` marker, used
        // to wedge the watcher: the `(?:\\n|[\s\S])*?` alternation in PemBlockRegex had two paths
        // for every `\n` pair, producing ~2^N backtracking on the failed-to-find-END search.
        // Observed in prod: watcher main loop at 100% CPU for 50s+ on a 5KB line.
        var keyBody = new StringBuilder();

        // Synthetic base64-like body — NOT a real key. The redactor's backtracking shape only
        // depends on the count of `\n` pairs and absence of an END marker, not on byte contents.
        for (var i = 0; i < 60; i++) {
            keyBody.Append("AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKKLLLLMMMMNNNNOOOOPPPP\\n");
        }

        var line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_1\",\"type\":\"tool_result\",\"content\":\"-----BEGIN RSA PRIVATE KEY-----\\n"
         + keyBody
         + "\",\"is_error\":false}]}}";

        var sw = Stopwatch.StartNew();
        var result = SecretRedactor.RedactLine(line);
        sw.Stop();

        // The pre-fix regex would not return inside any reasonable test budget on this input.
        // 2 seconds is generous; the fixed regex completes in <10ms.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        // No END marker means there's no PEM block to redact — line should pass through unchanged.
        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsLine_OverSizeLimit_ReplacesWithPlaceholder_DoesNotLeakContent() {
        // Defense in depth: lines above the size cap skip the regex pipeline entirely so a
        // future regex change reintroducing ambiguity cannot wedge the watcher. The line MUST
        // NOT be returned verbatim — WatchCommand uploads RedactLine output to the server, so
        // raw passthrough on an oversize tool-result line would be an exfiltration path.
        var secretMarker = "GHOST_TOKEN_DO_NOT_LEAK_ME_xyz12345";
        var padding      = new string('A', SecretRedactor.MaxRedactableLineChars);
        var oversized    = padding + secretMarker;

        var sw = Stopwatch.StartNew();
        var result = SecretRedactor.RedactLine(oversized);
        sw.Stop();

        await Assert.That(result).DoesNotContain(secretMarker);
        await Assert.That(result).IsEqualTo(SecretRedactor.OversizeLinePlaceholder);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(100));
    }

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
    public async Task LeavesAssistantText_Unchanged_WhenNoSecretMatch() {
        // Plain assistant prose with no secret-matching tokens — scanned, but nothing to redact.
        var line = """{"type":"progress","data":{"message":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}},"uuid":"abc","timestamp":"2026-01-01T00:00:00Z"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task LeavesMalformedJson_Unchanged_WhenNoSecretMatch() {
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
    [Arguments("dckr_pat_ABCDEFghijklmnop1234567890", "Docker Hub PAT")]
    [Arguments("dckr_oat_J6Ui9AbcDef123XYZabcdef0987", "Docker Hub OAuth access token")]
    public async Task RedactsLine_VendorToken_InToolResult(string token, string description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"The token is {{{token}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(token);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_TaskNotificationTag_MidWordSkMatch() {
        // the OpenAI `sk-` vendor-token branch matched the `sk-notification` substring
        // inside "ta·sk-notification", turning Claude Code's injected background-task blocks into
        // `<ta[REDACTED]> … </ta[REDACTED]>`. The tag is not a secret and must survive verbatim.
        var line = """
            {"type":"user","message":{"role":"user","content":"<task-notification>\n<task-id>by9prly15</task-id> <tool-use-id>toolu_01BBj6kq3JaFfKHG93BEmqnj</tool-use-id> <status>completed</status> <summary>Background command \"Continue watching server CI\" completed (exit code 0)</summary>\n</task-notification>"}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
        await Assert.That(result).Contains("<task-notification>");
        await Assert.That(result).Contains("</task-notification>");
        await Assert.That(result).DoesNotContain("[REDACTED]");
    }

    [Test]
    [Arguments("the disk-usage report shows 80% full")] // "sk-" inside "disk-"
    [Arguments("run a task-notification handler on completion")]
    [Arguments("brisk-walking is good; kiosk-mode too")]
    public async Task DoesNotRedact_SkPrefix_MidWord(string prose) {
        // The `sk-` prefix must only match at a token boundary, never mid-identifier.
        var line = $$$"""
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{{{prose}}}"}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    [Arguments("space before: sk-proj-ABCDEFghijklmnop123", "sk-proj-ABCDEFghijklmnop123")]
    [Arguments("(sk-live_00000000000FAKEFAKE00)", "sk-live_00000000000FAKEFAKE00")]
    [Arguments("key=sk-test_00000000000FAKEFAKE00 end", "sk-test_00000000000FAKEFAKE00")]
    public async Task RedactsLine_SkToken_AtBoundary_StillRedacts(string content, string token) {
        // The boundary fix must NOT weaken redaction of genuine sk- keys preceded by a
        // non-alphanumeric char (space, punctuation, `=`).
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{{{content}}}","is_error":false}]}}
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
    public async Task RedactsLine_AwsUniqueId_InToolResult(string id, string description) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"id is {{{id}}} here","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        // Assert full redaction with adjacent-character locked down — a partial leak like
        // "[REDACTED]5" (21-char ID redacted as 20) would still pass DoesNotContain(id).
        await Assert.That(result).Contains("id is [REDACTED] here");
        await Assert.That(result).DoesNotContain(id);
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
    [Arguments("client-secret: a8f3b2c91d4e7f0123456789abcdef01", "a8f3b2c91d4e7f0123456789abcdef01")] // hyphen in key
    [Arguments("aws.secret.access.key: AKIAIOSFODNN7EXAMPLE99", "AKIAIOSFODNN7EXAMPLE99")]               // dots in key
    public async Task RedactsLine_YamlStyleSecret_KeyWithSeparators_InToolResult(string kv, string secret) {
        // The key gap is `[\w.\-]*`, so hyphen- and dot-separated key names still match.
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{{{kv}}}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(secret);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_YamlStyleSecret_ProseColonAfterUpstreamKeyword() {
        // Regression: a secret keyword anywhere earlier in a prose sentence used to reach across the
        // old `[^:\n]*` gap to an unrelated prose colon, redacting the next 8+-char word. Here
        // "access token" sits upstream and "model-id" (exactly 8 chars) followed "real risk:" — it
        // was wrongly replaced with [REDACTED]. The `[\w.\-]*` gap requires the keyword to be part of
        // the key token, so this prose now passes through untouched.
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"We send the access token. The one real risk: model-id matching is fragile here."}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
        await Assert.That(result).Contains("model-id matching");
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
    public async Task LeavesUserText_Unchanged_WhenSecretValueTooShort() {
        // "hunter2" is below LabeledSecretRegex's 16-char floor; no pattern matches.
        // (A real high-entropy secret in the same shape WOULD be redacted — see
        // RedactsLine_VendorToken_InUserTextContent.)
        var line = """{"type":"user","message":{"role":"user","content":"my secret password is hunter2"}}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task LeavesToolUse_Unchanged_WhenNoSecretMatch() {
        // tool_use args reference a file path; "/etc/secret.pem" has no key:value or labeled-secret
        // shape, so the line passes through scanning unmodified.
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

    // Scope-widening coverage — secrets must be redacted regardless of whether they appear in a
    // tool_result, assistant narrative, or human-authored user turn. The original implementation
    // only scanned user messages containing a tool_result block, which let the assistant trivially
    // re-emit a token by quoting it back when explaining what a command did.

    [Test]
    public async Task RedactsLine_DockerToken_InAssistantText() {
        // Real leak shape (AI-): agent paraphrased kubectl/docker output into its own narrative,
        // re-emitting a dckr_oat_ token that had been redacted from the originating tool_result.
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Image pushed but the pull token (dckr_oat_J6Ui9AbcDef123XYZabcdef0987) used by the cluster's registry-pull Secret is scoped to kurrentplatform/kcap only."}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("dckr_oat_J6Ui9AbcDef123XYZabcdef0987");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_VendorToken_InAssistantText_ProgressFormat() {
        var line = """
            {"type":"progress","data":{"message":{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I used ghp_ABCDEFghijklmnop1234567890abcdef12345678 to authenticate."}]}}},"uuid":"abc","timestamp":"2026-01-01T00:00:00Z"}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_ABCDEFghijklmnop1234567890abcdef12345678");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_VendorToken_InUserTextContent() {
        // Free-form user turn (string content, not the tool_result array form) — a human paste of
        // a token must also be redacted before the line is uploaded.
        var line = """
            {"type":"user","message":{"role":"user","content":"my token is ghp_ABCDEFghijklmnop1234567890abcdef12345678 please rotate"}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_ABCDEFghijklmnop1234567890abcdef12345678");
        await Assert.That(result).Contains("[REDACTED]");
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

    // auth headers recorded as-is

    [Test]
    public async Task RedactsLine_AuthorizationBearer_InCurlToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"$ curl -fsS -H \"Authorization: Bearer abc123def456ghi789jkl012mno\" https://api.example.com\n{\"ok\":true}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl012mno");
        await Assert.That(result).Contains("Authorization: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AuthorizationBasic_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"Authorization: Basic dXNlcjpwYXNzd29yZA==","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("dXNlcjpwYXNzd29yZA==");
        await Assert.That(result).Contains("Authorization: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_CookieHeader_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"> Cookie: session=abc123def456; csrf=xyz789uvw321","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("session=abc123def456");
        await Assert.That(result).DoesNotContain("csrf=xyz789uvw321");
        await Assert.That(result).Contains("Cookie: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_XApiKeyHeader_InToolResult() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"X-Api-Key: opaque_value_with_no_known_prefix_12345","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("opaque_value_with_no_known_prefix_12345");
        await Assert.That(result).Contains("X-Api-Key: [REDACTED]");
    }

    [Test]
    public async Task RedactsLine_AuthorizationHeader_AsJsonObjectField() {
        // HTTP request logged as a JSON object (escaped quotes around key and value).
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"request headers: { \"Authorization\": \"Bearer abc123def456ghi789jkl012mno\", \"Accept\": \"application/json\" }","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abc123def456ghi789jkl012mno");
        await Assert.That(result).Contains("[REDACTED]");

        // Result must remain valid JSON after redaction.
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("is_error").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task RedactsLine_AuthorizationHeader_AsRealJsonObject() {
        // Real JSON object (NOT a string-encoded one) with Authorization as a true key.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"ok"}]},"toolUseResult":{"headers":{"Authorization":"Bearer opaque-token-1234567890abc"}}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("opaque-token-1234567890abc");
        await Assert.That(result).Contains("[REDACTED]");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("toolUseResult").GetProperty("headers").GetProperty("Authorization").GetString()).IsEqualTo("[REDACTED]");
    }

    [Test]
    [Arguments("Private-Token", "glpat-AAAAAAAAAAAAAAAAAAAA")]
    [Arguments("Job-Token", "ci_job_token_value_1234567890")]
    [Arguments("Deploy-Token", "deploy_token_value_1234567890")]
    [Arguments("X-Vault-Token", "hvs.AAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments("X-Consul-Token", "00000000-0000-0000-0000-000000000000")]
    [Arguments("X-CSRF-Token", "csrf_value_abcdef1234567890")]
    [Arguments("X-XSRF-TOKEN", "xsrf_value_abcdef1234567890")]
    [Arguments("X-Hub-Signature-256", "sha256=abcdef1234567890abcdef1234567890")]
    [Arguments("X-Slack-Signature", "v0=abcdef1234567890abcdef1234567890")]
    [Arguments("Stripe-Signature", "t=1492774577,v1=5257a86")]
    [Arguments("X-Registry-Auth", "ewogICJhdXRoIjogImFiY2QifQo=")]
    public async Task RedactsLine_AdditionalAuthHeaders_InToolResult(string header, string value) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{{{header}}}: {{{value}}}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(value);
        await Assert.That(result).Contains($"{header}: [REDACTED]");
    }

    // Finding 4 coverage — URL query secrets and userinfo

    [Test]
    [Arguments("https://example.com/path?access_token=at_abcdef1234567890&user=alice")]
    [Arguments("https://example.com/cb?id_token=jwt.eyJhbGciOi.payload&state=xyz")]
    [Arguments("https://api.example.com/x?signature=abcdef1234567890abcdef&v=1")]
    [Arguments("https://s3.amazonaws.com/b/k?AWSAccessKeyId=AKIAIOSFODNN7EXAMPLE&Signature=abc%2F123&Expires=1")]
    [Arguments("https://example.com/h?api_key=key_abcdef1234567890")]
    public async Task RedactsLine_UrlQuerySecret_InToolResult(string url) {
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"GET {{{url}}} -> 200","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).Contains("[REDACTED]");
        // The secret param value must not survive — check by ensuring no `[?&]<param>=<longvalue>` is left.
        await Assert.That(result).DoesNotContain("at_abcdef1234567890");
        await Assert.That(result).DoesNotContain("jwt.eyJhbGciOi.payload");
        await Assert.That(result).DoesNotContain("abcdef1234567890abcdef");
        await Assert.That(result).DoesNotContain("AKIAIOSFODNN7EXAMPLE");
        await Assert.That(result).DoesNotContain("key_abcdef1234567890");
    }

    [Test]
    public async Task RedactsLine_UrlUserinfo_InToolResult() {
        // RFC 3986 userinfo: literal `@` must be percent-encoded, so the password component cannot
        // legally contain `@`. Realistic test case is a Git URL with an opaque-token password.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"cloning https://alice:ghp_abcdef1234567890ABCDEF@github.com/org/repo.git","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abcdef1234567890ABCDEF");
        await Assert.That(result).Contains("https://alice:[REDACTED]@github.com");
    }

    // labeled secrets with no colon separator (e.g. `hcloud:token  <value>`)

    [Test]
    public async Task RedactsLine_LabeledSecret_HcloudToken_InToolResult() {
        // `hcloud:token` is a label, not a key:value separator. Token value below is a synthetic
        // 64-char placeholder matching Hetzner's format — never paste a real token here.
        const string fakeHcloudToken = "FAKE_HCLOUD_TOKEN_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        var line = $$$"""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"hcloud:token  {{{fakeHcloudToken}}}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain(fakeHcloudToken);
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_LabeledSecret_PasswordWithSpace() {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"password    supersecretvalue1234567890","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("supersecretvalue1234567890");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_LabeledSecret_PasswordWithPunctuation() {
        // Value with `!`, `@`, `#`, etc. — broader charset must catch the full opaque value, not
        // stop at the first non-alnum char.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"password abcdefghijklmnop!tail#more$end","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("abcdefghijklmnop!tail#more$end");
        await Assert.That(result).DoesNotContain("!tail");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task DoesNotRedact_LabeledSecret_ProseAboutSecrets() {
        // Negative — prose about secrets must survive. Short word "might" after "token" stops the
        // 16+ run, and there are no header keywords either.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"the token might be invalid; refresh it","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
    }

    [Test]
    public async Task DoesNotRedact_LabeledSecret_ShortValue() {
        // Negative — values below the 16-char floor are noise (version numbers, IDs, etc.).
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"token short1234","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);
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

    // Structural redaction — a pattern may only rewrite the inside of a string value, never the
    // JSON around it. The server drops a line it cannot parse, so corruption here is a silent
    // hole in the transcript rather than a visible failure.

    [Test]
    [Arguments("""{"s":"plain","n":[7,{"deep":"x"}]}""", """{"s":"[REDACTED]","n":[7,{"deep":"[REDACTED]"}]}""")]
    [Arguments("""["Bearer one","Bearer two"]""", """["[REDACTED]","[REDACTED]"]""")]
    [Arguments("""{"a":1}""", """{"a":1}""")]
    [Arguments("true", "true")]
    [Arguments("null", "null")]
    public async Task RedactsEveryStringLeaf_UnderASecretKey_KeepingItsShape(string value, string expected) {
        // A secret-bearing key hands its secret to the whole subtree, not just to a string sitting
        // directly under it. Structure survives so the line still parses, and every string leaf
        // under it goes; a number, a boolean and null are left as they are.
        var line     = """{"type":"user","toolUseResult":{"payload":{"cookie":""" + value + ""","items":[1,2]}}}""";
        var expected_ = """{"type":"user","toolUseResult":{"payload":{"cookie":""" + expected + ""","items":[1,2]}}}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(expected_);

        // The sibling is outside the armed subtree, so the disarm on the closing token landed.
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var payload = doc.RootElement.GetProperty("toolUseResult").GetProperty("payload");
        await Assert.That(payload.GetProperty("items").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"token":"abc123","public":"visible","n":42}""",
               """{"token":"[REDACTED]","public":"visible","n":42}""")]
    [Arguments("""{"token":{"inner":"abc123","num":7},"public":"visible","n":42}""",
               """{"token":{"inner":"[REDACTED]","num":7},"public":"visible","n":42}""")]
    [Arguments("""{"headers":{"Authorization":["Bearer x","Bearer y"],"z":1},"tail":"keep"}""",
               """{"headers":{"Authorization":["[REDACTED]","[REDACTED]"],"z":1},"tail":"keep"}""")]
    [Arguments("""{"ghp_abc123def456ghi789jkl012":"repo-name","public":"visible"}""",
               """{"[REDACTED]-1":"repo-name","public":"visible"}""")]
    [Arguments("""{"cookie":{"c":"1"},"mid":"keep","secret":{"s":"2"},"after":"keep"}""",
               """{"cookie":{"c":"[REDACTED]"},"mid":"keep","secret":{"s":"[REDACTED]"},"after":"keep"}""")]
    public async Task LeavesSiblings_OfASecretKey_Untouched(string line, string expected) {
        // What arms a subtree has to disarm on the way out. A flag that outlived its own key would
        // take the next sibling with it.
        await Assert.That(SecretRedactor.RedactLine(line)).IsEqualTo(expected);
    }

    [Test]
    public async Task NeverThrows_AndAlwaysEmitsParseableJson_OverGeneratedDocuments() {
        // RedactLine runs inside the drain's `Select`, so a throw takes transcript forwarding with
        // it, and a line that no longer parses is dropped by the server without a word. Seeded, so
        // a failure here is reproducible rather than a flake.
        string[] fragments = [
            "\"plain text\"", "\"ghp_abc123def456ghi789jkl012\"", "\"AKIAIOSFODNN7EXAMPLE\"",
            "\"Authorization: Bearer abc123def456\"", "\"caf\\u00e9 <tag> a/b\"", "\"\"",
            "1", "-0.0", "1e3", "123456789012345678901234567890", "true", "false", "null",
            "\"{\\\"api_key\\\":\\\"sk-live_abcdefghijkl\\\"}\"",
            "\"-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIB\\n-----END RSA PRIVATE KEY-----\"",
        ];
        string[] keys = ["a", "token", "cookie", "author", "privateKeys", "auth", "x", "a"];

        var rng      = new Random(20260827);
        var changed  = 0;

        for (var i = 0; i < 3000; i++) {
            var line = Compose(rng, fragments, keys, depth: 0);

            var result = SecretRedactor.RedactLine(line);

            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (!string.Equals(result, line, StringComparison.Ordinal)) changed++;
        }

        // Without this the corpus could stop redacting anything and the test would still pass.
        await Assert.That(changed).IsGreaterThan(1000);

        static string Compose(Random rng, string[] fragments, string[] keys, int depth) {
            if (depth >= 4 || rng.Next(3) == 0) return fragments[rng.Next(fragments.Length)];

            var count = rng.Next(1, 4);
            if (rng.Next(2) == 0) {
                var parts = new string[count];
                for (var i = 0; i < count; i++) parts[i] = Compose(rng, fragments, keys, depth + 1);

                return "[" + string.Join(",", parts) + "]";
            }

            var pairs = new string[count];
            for (var i = 0; i < count; i++)
                pairs[i] = "\"" + keys[rng.Next(keys.Length)] + "\":" + Compose(rng, fragments, keys, depth + 1);

            return "{" + string.Join(",", pairs) + "}";
        }
    }

    [Test]
    [Arguments("privateKey")]
    [Arguments("private_key")]
    [Arguments("private-key")]
    [Arguments("private.key")]
    [Arguments("privatekeys")]
    [Arguments("apiKeys")]
    [Arguments("accessKey")]
    [Arguments("clientSecrets")]
    [Arguments("credential")]
    [Arguments("authTokens")]
    public async Task RecognisesASecretKey_HoweverItIsSpelled(string key) {
        var line = "{\"cfg\":{\"" + key + "\":\"s3cr3t-value-here\"},\"n\":1}";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo("{\"cfg\":{\"" + key + "\":\"[REDACTED]\"},\"n\":1}");
    }

    [Test]
    [Arguments("""{"auth":{"tok":"abc"},"n":1}""", """{"auth":{"tok":"[REDACTED]"},"n":1}""")]
    [Arguments("""{"oauth":"abc","n":1}""", """{"oauth":"[REDACTED]","n":1}""")]
    [Arguments("""{"author":"Ada Lovelace","n":1}""", """{"author":"Ada Lovelace","n":1}""")]
    [Arguments("""{"authored_by":"Ada","authority":"local"}""", """{"authored_by":"Ada","authority":"local"}""")]
    public async Task TreatsBareAuthAsASecretKey_WithoutCatchingAuthor(string line, string expected) {
        // `auth` carries a token often enough to belong in the key vocabulary, and the end anchor
        // is the whole reason it can: unanchored it would swallow `author` and every word built
        // on it.
        await Assert.That(SecretRedactor.RedactLine(line)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"X-Api-Key":12345,"next":"kept"}""")]
    [Arguments("""{"Authorization":12345}""")]
    [Arguments("""{"cookie":{"n":5},"keep":7}""")]
    [Arguments("""{"password":1234567}""")]
    [Arguments("""{"oauth":2,"needs_auth":0}""")]
    public async Task LeavesEveryNumber_HoweverSecretItsKey(string line) {
        // Deliberate, and it gives up the all-digit credential: the keyword vocabulary matches
        // anywhere in a name, so the token-usage fields classify as secret-bearing on nearly every
        // model turn, while a header value arrives as a string and so is never a number here.
        await Assert.That(SecretRedactor.RedactLine(line)).IsEqualTo(line);
    }

    [Test]
    [Arguments("""{"type":"token_count","input_tokens":1234,"output_tokens":56}""")]
    [Arguments("""{"total_token_usage":{"input_tokens":1234,"cached":10},"model":"gpt-5"}""")]
    [Arguments("""{"usage":{"prompt_tokens":10,"completion_tokens":20}}""")]
    public async Task LeavesTokenMetrics_Alone(string line) {
        // `input_tokens` and `token_count` classify as secret-bearing on the keyword vocabulary,
        // and they sit on nearly every Codex turn. A number carries no credential, so none of them
        // is rewritten — the analytics that read these would otherwise see a redacted value.
        await Assert.That(SecretRedactor.RedactLine(line)).IsEqualTo(line);
    }

    [Test]
    public async Task RedactsSecretsAppearingAsPropertyNames_WithDistinctMarkers() {
        // A name that is itself a token has to go, and two of them cannot both become the bare
        // marker: that would collapse the object into a duplicate key.
        var line = """{"ghp_abc123def456ghi789jkl012":"repo","AKIAIOSFODNN7EXAMPLE":"acct","keep":"v"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo("""{"[REDACTED]-1":"repo","[REDACTED]-2":"acct","keep":"v"}""");
    }

    [Test]
    public async Task PreservesJson_WhenPemMarkersSpanTwoStringValues() {
        // `BEGIN` in one value and `END` in another, at different depths, are two values — not one
        // block — so there is nothing to redact and every structural token between them stays.
        var line = """{"outer":{"head":"-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIB"},"tail":["x",{"rest":"more\n-----END RSA PRIVATE KEY-----"}]}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).IsEqualTo(line);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("tail").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task RedactsLine_SecretsInsideEscapedInnerJson() {
        // A serialized tool result carried as a string, itself carrying a serialized body: the
        // patterns run over the decoded value, so both the once-escaped header and the
        // twice-escaped key/value pair are still reachable.
        var line = """
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"{\"headers\":{\"X-Api-Key\":\"opaque_value_no_prefix_123456\"},\"body\":\"{\\\"client_secret\\\":\\\"a8f3b2c91d4e7f0123456789abcdef01\\\"}\"}","is_error":false}]}}
            """.Trim();

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("opaque_value_no_prefix_123456");
        await Assert.That(result).DoesNotContain("a8f3b2c91d4e7f0123456789abcdef01");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content")[0];
        await Assert.That(content.GetProperty("is_error").GetBoolean()).IsFalse();
        // The inner document survives as text, minus the two values.
        await Assert.That(content.GetProperty("content").GetString()).Contains("\"headers\"");
    }

    [Test]
    public async Task RedactsLine_SecretKeyValue_ContainingEscapes_LeavesNoTail() {
        // A real JSON pair is two tokens, so a `"key": "value"` text pattern cannot see the key at
        // all: the property name decides, and the whole value goes — a value carrying quotes or
        // backslashes must not survive from its first escape onward.
        var line = """{"type":"user","toolUseResult":{"client_secret":"a\"b\\c-1234567890"}}""";

        var result = SecretRedactor.RedactLine(line);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("toolUseResult").GetProperty("client_secret").GetString())
            .IsEqualTo("[REDACTED]");
    }

    [Test]
    public async Task RedactedOutput_AlwaysParses_WhenInputParsed() {
        // Round-trip property over the shapes that broke text-level redaction: whatever a pattern
        // matches, a line that parsed on the way in parses on the way out.
        string[] corpus = [
            """{"h":{"cookie":{"a":1},"items":[1,2]}}""",
            """{"h":{"authorization":[1,2],"next":"keep"}}""",
            """{"h":{"set-cookie":null,"x-api-key":true,"n":7}}""",
            """{"h":{"x-csrf-token":{"nested":{"deep":[1,{"a":"b"}]}}}}""",
            """{"a":"-----BEGIN RSA PRIVATE KEY-----\nMIIE","b":["x",{"c":"z\n-----END RSA PRIVATE KEY-----"}]}""",
            """{"c":"Authorization: Bearer abc123def456ghi789jkl012mno","d":"tail"}""",
            """{"c":"{\"Authorization\": \"Bearer abc123def456ghi789\", \"Accept\": \"application/json\"}"}""",
            """{"c":"Set-Cookie: sid=\"quoted-value-1234567890\"; Path=/","d":1}""",
            """{"c":"first 100 chars: {\n  \"SecretAccessKey\": \"0X+FPiUxddhI7babryQv4l5JQ37Smuy"}""",
            """{"client_secret":"a\"b\\c-1234567890","keep":[1,2,3]}""",
            """{"c":"password    supersecretvalue1234567890 — ünïcode ✓ <tag> & more"}""",
            """{"c":"GITHUB_TOKEN=ghp_abc123def456ghi789jkl012mno345pqrs678\nAWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE"}""",
        ];

        var unparsable = corpus
            .Select(SecretRedactor.RedactLine)
            .Where(redacted => !Parses(redacted))
            .ToArray();

        await Assert.That(unparsable).IsEmpty();

        // The corpus is only meaningful if it actually exercises the patterns.
        await Assert.That(corpus.Count(line => SecretRedactor.RedactLine(line) != line)).IsGreaterThan(5);
        return;

        static bool Parses(string text) {
            try {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                return true;
            } catch (System.Text.Json.JsonException) {
                return false;
            }
        }
    }

    // Fidelity of everything the redactor did NOT touch. Copying untouched bytes through is what
    // buys these; a DOM or a re-serializing writer has to reproduce each one deliberately.

    [Test]
    [Arguments("1.0", "trailing zero")]
    [Arguments("1e3", "exponent form")]
    [Arguments("123456789012345678901234567890", "beyond every fixed-width numeric type")]
    [Arguments("0.1000000000000000055511151231257827", "more digits than a double round-trips")]
    [Arguments("-0.0", "negative zero")]
    public async Task PreservesNumberText_WhenAnotherValueIsRedacted(string number, string shape) {
        // Redacting one value must not reformat a number elsewhere in the line: a transcript
        // number is data, and rewriting it through a numeric type would silently alter it.
        var line = $$"""{"a":{{number}},"tok":"ghp_abc123def456ghi789jkl012mno345"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).Contains(number);
    }

    [Test]
    public async Task RedactsLine_WithDuplicatePropertyNames() {
        // JSON permits duplicate keys and transcripts are written by third-party tooling, so a
        // line carrying them must still redact rather than throw at the drain.
        var line = """{"a":1,"a":2,"tok":"ghp_abc123def456ghi789jkl012mno345"}""";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    public async Task RedactsLine_NestedDeeperThanTheReaderAllows() {
        // Nested past the depth System.Text.Json itself refuses, so nothing can walk it and the
        // whole-line pipeline is all there is. It still has to come back redacted: the alternative
        // is shipping the secret raw.
        var line = string.Concat(Enumerable.Repeat("""{"a":""", 1100))
          + "\"tok ghp_abc123def456ghi789jkl012mno345\""
          + new string('}', 1100);

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).Contains("[REDACTED]");
    }

    [Test]
    [Arguments("""{"a":1 /* ghp_abc123def456ghi789jkl012 */}""")]
    [Arguments("""{"a":1} // ghp_abc123def456ghi789jkl012""")]
    [Arguments("""{"a":1 /* nothing sensitive */}""")]
    public async Task DropsAComment_RatherThanShipALineNoStrictParserTakes(string line) {
        // A comment is not a value, so nothing else scans its text, and it cannot survive into
        // strict JSON either. Dropping counts as a change on its own — otherwise a line whose
        // values are all clean rides the unchanged path to the wire with the comment still on it.
        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123def456ghi789jkl012");
        await Assert.That(result).DoesNotContain("nothing sensitive");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("a").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task LeavesNonJsonLine_ToTheWholeLinePipeline() {
        // The drop only applies to a line that was JSON going in. Text that never parsed has
        // nothing to corrupt, and replacing it with a placeholder would lose it for no gain.
        var line = "plain log output with GITHUB_TOKEN=ghp_abc123def456ghi789jkl012 in it";

        var result = SecretRedactor.RedactLine(line);

        await Assert.That(result).DoesNotContain("ghp_abc123");
        await Assert.That(result).StartsWith("plain log output with GITHUB_TOKEN=");
    }
}
