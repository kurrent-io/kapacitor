namespace kapacitor;

static class ArgParsing {
    /// <summary>
    /// Resolves a positional sessionId from a command's argument list, falling
    /// back to <c>KAPACITOR_SESSION_ID</c>. Value-bearing flags (e.g.
    /// <c>--model sonnet</c>) must be declared via <paramref name="valueFlags"/>
    /// so their values aren't mistaken for the sessionId.
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

        return Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID");
    }
}
