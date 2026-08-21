namespace Capacitor.Cli;

static class ArgParsing {
    /// <summary>
    /// Resolves a positional sessionId from a command's argument list, falling
    /// back to <c>KCAP_SESSION_ID</c> and then <c>CODEX_THREAD_ID</c>.
    /// Value-bearing flags (e.g. <c>--model sonnet</c>) must be declared via
    /// <paramref name="valueFlags"/> so their values aren't mistaken for the
    /// sessionId.
    /// </summary>
    internal static string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) {
        var knownValueFlags = valueFlags is null or { Length: 0 }
            ? null
            : new HashSet<string>(valueFlags, StringComparer.Ordinal);

        for (var i = skipCount; i < args.Length; i++) {
            var token = args[i];
            if (token.StartsWith("--")) {
                if (knownValueFlags?.Contains(token) == true && i + 1 < args.Length) {
                    i++; // skip the value as well
                }

                continue;
            }

            return token;
        }

        return ResolveSessionIdFromEnv();
    }

    /// <summary>
    /// Resolves a sessionId purely from environment variables. Prefers
    /// <c>KCAP_SESSION_ID</c> and falls back to <c>CODEX_THREAD_ID</c>.
    /// Dashes are stripped from the returned value so callers don't have to.
    /// </summary>
    internal static string? ResolveSessionIdFromEnv() =>
        ResolveSessionIdFromEnv(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Env-injected overload of <see cref="ResolveSessionIdFromEnv()"/>, so callers that
    /// resolve ambient session state can be unit-tested without mutating the process
    /// environment (which forces whole-class serialization).
    /// </summary>
    internal static string? ResolveSessionIdFromEnv(Func<string, string?> getEnv) {
        var kcapId = getEnv("KCAP_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(kcapId))
            return kcapId.Replace("-", "");

        var codex = getEnv("CODEX_THREAD_ID");
        return !string.IsNullOrWhiteSpace(codex) ? codex.Replace("-", "") : null;
    }

    /// <summary>
    /// Validates that <paramref name="input"/> parses as a GUID and returns its
    /// canonical dashless 32-hex-character form. Use this at sites that consume
    /// the session ID as a filesystem path component (e.g. <c>kcap hide</c>,
    /// <c>kcap disable</c>), where slugs and path-traversal characters are
    /// not acceptable input.
    /// </summary>
    internal static bool TryNormalizeSessionGuid(string input, out string canonical) {
        if (Guid.TryParse(input, out var guid)) {
            canonical = guid.ToString("N");
            return true;
        }
        canonical = string.Empty;
        return false;
    }
}
