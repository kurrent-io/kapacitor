using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNext.Buffers;

namespace Capacitor.Cli;

static partial class SecretRedactor {
    // Lines above this are swapped for a placeholder rather than scanned: they are almost always
    // truncated dumps, and an unterminated `-----BEGIN RSA PRIVATE KEY-----` blob drives the regex
    // alternation into catastrophic backtracking that wedges the watcher loop at 100% CPU. UTF-16
    // units, not bytes — the cost tracks regex steps over chars, not wire size.
    internal const int MaxRedactableLineChars = 64 * 1024;

    internal const string RedactedMarker = "[REDACTED]";

    // The server treats an unknown top-level `type` as a no-op, so line numbering stays stable for
    // resume and gap recovery while no raw bytes leave the host.
    internal const string OversizeLinePlaceholder =
        """{"type":"redacted_oversize_line","reason":"line exceeded SecretRedactor size limit"}""";

    // Sent when the writer refuses what the reader handed it. The raw line would re-expose what
    // was already matched, and unparseable JSON the server drops without a word.
    internal const string UnparsableOutputPlaceholder =
        """{"type":"redacted_unparsable_line","reason":"redacted line was no longer valid JSON"}""";

    public static string RedactLine(string rawJsonlLine) {
        if (rawJsonlLine.Length > MaxRedactableLineChars) return OversizeLinePlaceholder;

        try {
            return RedactJsonStringValues(rawJsonlLine) ?? rawJsonlLine;
        } catch (JsonException) {
            // Not JSON, or nested past the depth System.Text.Json itself will not exceed. Nothing
            // can walk it structurally, so the whole-line pipeline is all that is left.
            return RedactSecrets(rawJsonlLine);
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            // The writer refused a token, or the loop met one it cannot re-emit.
            return UnparsableOutputPlaceholder;
        }
    }

    // Each value is scanned decoded, never as it sits in the line: a pattern run over the line
    // matches past the value it found into the surrounding structure. Null means nothing changed,
    // so the caller returns the line it already has rather than one the writer re-encoded.
    static string? RedactJsonStringValues(string line) {
        // An empty document makes the reader throw rather than read no tokens.
        if (line.Length == 0) return null;

        using var input  = new PoolingArrayBufferWriter<byte>();
        using var output = new PoolingArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, WriterOptions);

        // Unescaping only shortens, so the line's own length bounds any value decoded out of it.
        var decoded = ArrayPool<char>.Shared.Rent(line.Length);

        try {
            Encoding.UTF8.GetBytes(line, input);

            return Rewrite(input.WrittenArray.AsSpan(), decoded, writer)
                ? Encoding.UTF8.GetString(output.WrittenMemory.Span)
                : null;
        } finally {
            ArrayPool<char>.Shared.Return(decoded);
        }
    }

    // Mirrors every token back out, so the writer's own validation is what guarantees the result
    // parses. True when at least one value was replaced.
    static bool Rewrite(ReadOnlySpan<byte> utf8, Span<char> decoded, Utf8JsonWriter writer) {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);

        var redactedAny = false;
        var keyIsSecret = false;
        var redactedKeys = 0;

        // Depth of the container a secret-bearing key opened, or -1. A secret key whose value is an
        // object or array hands its secret to every leaf beneath it, not just to a string sitting
        // directly under it.
        var secretDepth = -1;

        while (reader.Read()) {
            var inSecret = secretDepth >= 0;

            switch (reader.TokenType) {
                case JsonTokenType.PropertyName:
                    var name = decoded[..reader.CopyString(decoded)];
                    keyIsSecret = inSecret || SecretKeyNameRegex.IsMatch(name);

                    // A name that is itself a secret goes entirely, counter and all: siblings
                    // replaced by one shared marker would collide into a duplicate key.
                    if (IsSecretItself(name)) {
                        writer.WritePropertyName($"{RedactedMarker}-{++redactedKeys}");
                        redactedAny = true;
                    } else {
                        writer.WritePropertyName(name);
                    }

                    continue;

                case JsonTokenType.String:
                    var value = decoded[..reader.CopyString(decoded)];
                    if (Redact(value, keyIsSecret || inSecret) is { } clean) {
                        writer.WriteStringValue(clean);
                        redactedAny = true;
                    } else {
                        writer.WriteStringValue(value);
                    }

                    break;

                // A number is never a redaction target, however secret its key: the keyword
                // vocabulary matches anywhere in a name, and `input_tokens` and `token_count` sit on
                // nearly every model turn. Raw rather than re-written, because the writer respells
                // `1.0`, `1e3` and integers past long.MaxValue.
                case JsonTokenType.Number:
                    writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);

                    break;

                case JsonTokenType.StartObject:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer.WriteStartObject();

                    break;

                case JsonTokenType.StartArray:
                    if (keyIsSecret && !inSecret) secretDepth = reader.CurrentDepth;
                    writer.WriteStartArray();

                    break;

                // A container reports the same depth on the way out as on the way in, so this is
                // the one that closes the subtree its key armed.
                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    if (inSecret && reader.CurrentDepth == secretDepth) secretDepth = -1;

                    break;

                // Dropped, not re-emitted: strict JSON has no comments, so a line that keeps one is
                // a line the server will not parse. The drop counts as a change on its own, or the
                // unchanged path hands back the raw line with the comment, and whatever is in it,
                // still there.
                case JsonTokenType.Comment:
                    redactedAny = true;

                    continue;

                case JsonTokenType.True:  writer.WriteBooleanValue(true); break;
                case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                case JsonTokenType.Null:  writer.WriteNullValue(); break;

                // Skipping a token that carries a value would ship a document that parses with that
                // value missing. `None` is the only one left, and the reader never emits it.
                default: throw new InvalidOperationException($"Unhandled JSON token {reader.TokenType}.");
            }

            keyIsSecret = false;
        }

        writer.Flush();

        return redactedAny;
    }

    // 1000 is the writer's own ceiling, so anything the reader accepts the writer can emit, and the
    // whole-line pipeline is left with only input no reader would take. Comments are surfaced
    // rather than skipped so the loop sees that one was there and forces the rewrite that drops it.
    static readonly JsonReaderOptions ReaderOptions =
        new() { MaxDepth = 1000, CommentHandling = JsonCommentHandling.Allow };

    // Only the escapes JSON mandates, so `<`, `&` and non-ASCII survive as they arrived.
    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The replacement for one value, or null to keep it. Prefiltering before allocating is exact:
    // if no pattern matches the text, none of them changes it, so each later one sees that text.
    static string? Redact(ReadOnlySpan<char> value, bool keyIsSecret) {
        if (keyIsSecret) return value.SequenceEqual(RedactedMarker) ? null : RedactedMarker;
        if (!AnyPatternMatches(value)) return null;

        var text     = new string(value);
        var redacted = RedactSecrets(text);

        return string.Equals(redacted, text, StringComparison.Ordinal) ? null : redacted;
    }

    // A property name is matched only against the patterns that recognise a credential outright;
    // the rest look for a `key: value` or `key=value` shape, which is what a name sits opposite.
    // Running all eleven over every name instead costs about half the rewrite again. The length
    // gate is exact rather than a heuristic: the shortest match any of the three admits is `sk-`
    // plus its ten-character minimum.
    const int ShortestMatchableCredential = 13;

    static bool IsSecretItself(ReadOnlySpan<char> value) =>
        value.Length >= ShortestMatchableCredential
     && (VendorTokenRegex.IsMatch(value) || AwsUniqueIdRegex.IsMatch(value) || PemBlockRegex.IsMatch(value));

    static bool AnyPatternMatches(ReadOnlySpan<char> value) =>
        PemBlockRegex.IsMatch(value)
     || AwsUniqueIdRegex.IsMatch(value)
     || VendorTokenRegex.IsMatch(value)
     || AuthHeaderRegex.IsMatch(value)
     || UrlQuerySecretRegex.IsMatch(value)
     || UrlUserinfoRegex.IsMatch(value)
     || JsonKeySecretRegex.IsMatch(value)
     || EnvVarSecretRegex.IsMatch(value)
     || YamlStyleSecretRegex.IsMatch(value)
     || LabeledSecretRegex.IsMatch(value)
     || ConnectionStringPwdRegex.IsMatch(value);

    static string RedactSecrets(string text) {
        text = PemBlockRegex.Replace(text, RedactedMarker);
        text = AwsUniqueIdRegex.Replace(text, RedactedMarker);
        text = VendorTokenRegex.Replace(text, RedactedMarker);
        text = AuthHeaderRegex.Replace(text, "$1" + RedactedMarker);
        text = UrlQuerySecretRegex.Replace(text, "$1" + RedactedMarker);
        text = UrlUserinfoRegex.Replace(text, "$1" + RedactedMarker + "$3");
        text = JsonKeySecretRegex.Replace(text, "$1" + RedactedMarker + "$3");
        text = EnvVarSecretRegex.Replace(text, "$1" + RedactedMarker);
        text = YamlStyleSecretRegex.Replace(text, "$1" + RedactedMarker);
        text = LabeledSecretRegex.Replace(text, "$1" + RedactedMarker);
        text = ConnectionStringPwdRegex.Replace(text, "$1" + RedactedMarker + "$3");

        return text;
    }

    // Shared with the property-name check, which must recognise the same keys.
    // These keys are spelled every which way — `private_key`, `private-key`, `private.key`,
    // `privateKey`, `privateKeys` — so both the separator and the plural are optional. The trailing
    // `s?` earns its place in the two patterns that demand a word boundary after the keyword, where
    // `secrets` would otherwise not match at all. A literal space is deliberately not a separator:
    // the `key: value` patterns would then fire on prose like "the private key: rotate it monthly".
    const string SecretKeywords =
        "secrets?|tokens?|passwords?|passwd|pwd|api[-_.]?keys?|private[-_.]?keys?|credentials?|client[-_.]?secrets?|access[-_.]?keys?|auth[-_.]?tokens?";

    // HTTP auth-bearing headers — Authorization, cookies, CSRF and signature headers, API-key
    // variants. Shared with the property-name check for the same reason.
    const string AuthHeaderNames =
        "authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token|x-access-token|x-amz-security-token|x-amz-signature|x-goog-api-key|api-key|private-token|job-token|deploy-token|x-vault-token|x-consul-token|x-csrf-token|x-xsrf-token|x-hub-signature(?:-256)?|x-slack-signature|stripe-signature|x-registry-auth";

    // A key whose value is a secret outright. The in-text `"key": "value"` patterns cannot see a
    // real JSON pair — key and value are separate tokens — so both vocabularies run here instead.
    // Header names are suffix-anchored, because `X-Forwarded-Authorization` is as much an auth
    // header as `Authorization`; `auth` rides that anchor, which is what keeps it off `author`.
    [GeneratedRegex("(?:" + AuthHeaderNames + "|auth)$|(?:" + SecretKeywords + ")", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyNameRx();

    static readonly Regex SecretKeyNameRegex = SecretKeyNameRx();

    // Matches PEM private key blocks. `[\s\S]` already covers `\` and `n` individually, so a
    // `(?:\\n|[\s\S])` alternation would be both redundant and catastrophically backtrackable when
    // the BEGIN marker appears without a matching END (e.g. truncated tool dumps). The `{0,16384}`
    // upper bound caps the search even if a future regex change reintroduces ambiguity; real PEM
    // keys (RSA-4096 armored) are ~3.2KB so this leaves plenty of headroom.
    [GeneratedRegex(@"-----BEGIN[A-Z\s]*PRIVATE KEY-----[\s\S]{0,16384}?-----END[A-Z\s]*PRIVATE KEY-----", RegexOptions.None)]
    private static partial Regex PemBlockRx();

    static readonly Regex PemBlockRegex = PemBlockRx();

    // AWS unique ID prefixes (access keys, session tokens, IAM principals).
    // See: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_identifiers.html#identifiers-prefixes
    // Access keys (AKIA/ASIA) are 20 chars total; IAM principal unique IDs are typically 21 chars
    // but not strictly length-bounded, so match {16,128} with a non-alnum lookahead to avoid
    // leaving a trailing character adjacent to [REDACTED].
    [GeneratedRegex("(?:AKIA|ASIA|AROA|AIDA|AIPA|AGPA|ANPA|ANVA|ASCA|APKA|ABIA|ACCA)[0-9A-Z]{16,128}(?![0-9A-Z])", RegexOptions.None)]
    private static partial Regex AwsUniqueIdRx();

    static readonly Regex AwsUniqueIdRegex = AwsUniqueIdRx();

    // Known vendor token prefixes followed by token characters.
    // Each prefix is specific enough to avoid false positives — EXCEPT the bare `sk-` (OpenAI)
    // prefix, which is short enough to appear mid-word: unguarded it matches the `sk-notification`
    // substring inside "ta·sk-notification", redacting Claude Code's injected background-task
    // blocks to `<ta[REDACTED]> … </ta[REDACTED]>`. The non-alphanumeric lookbehind keeps the
    // `sk-` branch at a token boundary, so real keys (`sk-proj-…`, `sk-live_…`, preceded by
    // whitespace/quote/punctuation) still redact while `disk-`, `task-`, `kiosk-` pass through.
    // The other prefixes carry `_`/distinctive spellings and don't collide mid-word.
    [GeneratedRegex(@"(?:ghp_|gho_|ghs_|github_pat_|cfat_|(?<![A-Za-z0-9])sk-(?:proj-|live_|test_)?|sk_live_|sk_test_|xoxb-|xoxp-|xoxa-|pypi-|npm_|glpat-|dckr_pat_|dckr_oat_)[A-Za-z0-9\-_]{10,}", RegexOptions.None)]
    private static partial Regex VendorTokenRx();

    static readonly Regex VendorTokenRegex = VendorTokenRx();

    // JSON key: matches "secret_name": "value" or \"secret_name\": \"value\" (a value carrying
    // JSON that was itself serialized into a string keeps its escapes after one decode).
    // group 1 = opening quote(s) + key name + closing quote(s) + colon + space + opening quote(s)
    // group 2 = value
    // group 3 = closing quote(s), or nothing when the value runs to the end of a truncated dump
    [GeneratedRegex(
        """((?:\\"|")(?:[^"\\]*(?:""" + SecretKeywords + """)[^"\\]*)(?:\\"|")[ \t]*:[ \t]*(?:\\"|"))([^"\\]+)((?:\\"|")|$)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex JsonKeySecretRx();

    static readonly Regex JsonKeySecretRegex = JsonKeySecretRx();

    // Env var: SECRET_NAME=value (uppercase key containing secret keyword, value until whitespace
    // or a quote — a quote ends the value in every shell and JSON-ish dump it can appear in).
    [GeneratedRegex(@"([A-Z_]*(?:SECRETS?|TOKENS?|PASSWORDS?|PASSWD|PWD|API_?KEYS?|PRIVATE_?KEYS?|CREDENTIALS?|CLIENT_?SECRETS?|ACCESS_?KEYS?|AUTH_?TOKENS?)[A-Z_]*=)([^\s""\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvVarSecretRx();

    static readonly Regex EnvVarSecretRegex = EnvVarSecretRx();

    // YAML-style: secret_name: value (key containing secret keyword followed by colon, space, and
    // value). Minimum 8 chars to reduce false positives.
    //
    // The gap between the keyword and the colon is `[\w.\-]*` — only key-name characters. A
    // permissive `[^:\n]*` gap would match any run of non-colon chars, so a secret keyword
    // appearing anywhere earlier in a prose sentence reaches across to an unrelated prose colon
    // and redacts whatever 8+-char word follows it: `"...access token. The one real risk: model-id
    // matching"` loses `model-id` (exactly 8 chars). Constraining the gap to identifier characters
    // forces the keyword to actually be part of the `key:` token, while still allowing real keys
    // like `client-secret:`, `aws.secret.access.key:`, and `auth_token:`.
    [GeneratedRegex("""((?:""" + SecretKeywords + """)[\w.\-]*:[ \t]+)([^\s"\\]{8,})""", RegexOptions.IgnoreCase)]
    private static partial Regex YamlStyleSecretRx();

    static readonly Regex YamlStyleSecretRegex = YamlStyleSecretRx();

    // Connection string: Password=value; or Pwd=value;
    // group 1 = key=, group 2 = value, group 3 = ; or end
    [GeneratedRegex(@"((?:Password|Pwd)\s*=\s*)([^;""\\]+)(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPwdRx();

    static readonly Regex ConnectionStringPwdRegex = ConnectionStringPwdRx();

    // HTTP auth-bearing header carried inside text — redact the whole header value. `(?:\\?")?`
    // matches both the bare header line (`Authorization: …`) and a quoted JSON-object form found
    // in a dump of a request (`"Authorization": "…"`, or `\"Authorization\": \"…\"` when that dump
    // was serialized into a string more than once).
    //
    // The value stops at a quote, so a header whose value embeds one (`Set-Cookie: a="b"; …`)
    // redacts only up to it.
    //
    // group 1 = header name + colon + optional opening quote, group 2 = value
    [GeneratedRegex(
        """((?:""" + AuthHeaderNames + """)(?:\\?")?\s*:\s*(?:\\?")?\s*)([^\r\n"\\]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex AuthHeaderRx();

    static readonly Regex AuthHeaderRegex = AuthHeaderRx();

    // Labeled secret — secret keyword followed by whitespace (NOT a colon) and an opaque value.
    // Catches `hcloud:token  9xKMA…` and similar where the keyword is a label, not a key:value
    // separator. The colon form is already covered by YamlStyleSecretRegex.
    // 16-char minimum on `[^\s"\\]` value covers tokens with punctuation (e.g. `password p@ss!w0rd…`)
    // while the 16-char floor keeps prose like "the token might fail" from matching.
    // group 1 = keyword + whitespace, group 2 = value
    [GeneratedRegex("""\b((?:""" + SecretKeywords + """)\b[ \t]+)([^\s"\\]{16,})""", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledSecretRx();

    static readonly Regex LabeledSecretRegex = LabeledSecretRx();

    // URL query secrets — `?key=value` or `&key=value` where the param name is a known secret-bearing
    // key. Covers OAuth tokens, signed-URL signatures, AWS pre-signed URL params, and common API-key
    // query patterns. Stops at `&`, `#`, whitespace, or a quote.
    // group 1 = `[?&]key=`, group 2 = value
    [GeneratedRegex(
        """([?&](?:access_token|refresh_token|id_token|client_secret|signature|sig|x-amz-signature|awsaccesskeyid|api_key|apikey|api-key|token|password|secret|auth_token|sas)=)([^&\s"\\#]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex UrlQuerySecretRx();

    static readonly Regex UrlQuerySecretRegex = UrlQuerySecretRx();

    // URL userinfo — `https://user:password@host` form. Redacts the password component only.
    // group 1 = scheme + user + colon, group 2 = password, group 3 = @
    [GeneratedRegex(
        """(https?://[^:/\s"\\@]+:)([^@\s"\\/]+)(@)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex UrlUserinfoRx();

    static readonly Regex UrlUserinfoRegex = UrlUserinfoRx();
}
