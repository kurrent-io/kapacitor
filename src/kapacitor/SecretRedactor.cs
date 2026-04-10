using System.Text.Json;
using System.Text.RegularExpressions;

namespace kapacitor;

static partial class SecretRedactor {
    public static string RedactLine(string rawJsonlLine) {
        try {
            using var doc  = JsonDocument.Parse(rawJsonlLine);
            var       root = doc.RootElement;

            // Resolve the user message element from either format:
            //   Direct format:   { "type": "user", "message": { "role": "user", "content": [...] } }
            //   Progress format: { "type": "progress", "data": { "message": { "type": "user", "message": { ... } } } }
            JsonElement? userMessage;
            if (root.Str("type") == "user") {
                // Direct format — the root IS the user event; content lives at root.message.content
                userMessage = root;
            } else {
                // Progress wrapper — content lives at data.message.message.content
                var nested = root.Obj("data")?.Obj("message");
                if (nested is null || nested.Value.Str("type") != "user")
                    return rawJsonlLine;
                userMessage = nested;
            }

            var content = userMessage.Value.Obj("message")?.Arr("content");
            if (content is null)
                return rawJsonlLine;

            var hasToolResult = false;
            foreach (var block in content.Value.EnumerateArray()) {
                if (block.Str("type") == "tool_result") {
                    hasToolResult = true;
                    break;
                }
            }
            if (!hasToolResult)
                return rawJsonlLine;

            // We have tool_result blocks — redact the line as a raw string.
            // Working on the serialized JSON string lets us handle both the
            // content field and toolUseResult without manual tree rewriting.
            var redacted = RedactSecrets(rawJsonlLine);

            return redacted == rawJsonlLine ? rawJsonlLine : redacted;
        } catch {
            // Malformed JSON — pass through unchanged
            return rawJsonlLine;
        }
    }

    static string RedactSecrets(string text) {
        text = PemBlockRegex.Replace(text, "[REDACTED]");
        text = AwsAccessKeyRegex.Replace(text, "[REDACTED]");
        text = VendorTokenRegex.Replace(text, "[REDACTED]");
        return text;
    }

    // Matches PEM private key blocks (handles both real newlines and \\n escaped newlines in JSON strings)
    [GeneratedRegex(@"-----BEGIN[A-Z\s]*PRIVATE KEY-----(?:\\n|[\s\S])*?-----END[A-Z\s]*PRIVATE KEY-----", RegexOptions.None)]
    private static partial Regex PemBlockRx();
    static readonly Regex PemBlockRegex = PemBlockRx();

    // AWS access key IDs: AKIA followed by 16 uppercase alphanumeric chars
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.None)]
    private static partial Regex AwsAccessKeyRx();
    static readonly Regex AwsAccessKeyRegex = AwsAccessKeyRx();

    // Known vendor token prefixes followed by token characters
    // Each prefix is specific enough to avoid false positives
    [GeneratedRegex(@"(?:ghp_|gho_|ghs_|github_pat_|cfat_|sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-)[A-Za-z0-9\-_]{10,}", RegexOptions.None)]
    private static partial Regex VendorTokenRx();
    static readonly Regex VendorTokenRegex = VendorTokenRx();
}
