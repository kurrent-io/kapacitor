namespace kapacitor.Eval;

/// <summary>
/// Resolves <c>--questions</c>/<c>--skip</c> flag values against a fetched
/// taxonomy. Pure function so tests can exercise it without network.
///
/// <para>Return value is a tuple rather than an exception-based API because
/// flag validation errors flow to stderr + exit code 2, not to a crash.
/// Both flags set at once is an error (mutually exclusive). Null for both
/// returns the full catalog.</para>
/// </summary>
internal static class EvalQuestionSelection {
    public static (IReadOnlyList<EvalQuestionDto>? Questions, string? Error) Resolve(
            IReadOnlyList<EvalQuestionDto> catalog,
            IReadOnlyList<string>?         include,
            IReadOnlyList<string>?         skip
        ) {
        if (include is not null && skip is not null) {
            return (null, "--questions and --skip are mutually exclusive");
        }

        if (include is null && skip is null) {
            return (catalog, null);
        }

        var categories    = catalog.Select(q => q.Category).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        // Defensive TryAdd rather than ToDictionary: the server-side catalog
        // has unique IDs by construction, but a misbehaving server response
        // should surface a controlled error instead of crashing the CLI.
        var questionsById = new Dictionary<string, EvalQuestionDto>(StringComparer.Ordinal);
        foreach (var q in catalog) {
            if (!questionsById.TryAdd(q.Id, q)) {
                return (null, $"eval question catalog contains duplicate id '{q.Id}'");
            }
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var tokens   = include ?? skip!;
        foreach (var token in tokens) {
            if (categories.Contains(token)) {
                foreach (var q in catalog) {
                    if (q.Category == token) resolved.Add(q.Id);
                }
            } else if (questionsById.ContainsKey(token)) {
                resolved.Add(token);
            } else {
                return (null, $"unknown question or category '{token}'. Run `kapacitor eval --list-questions` to see available tokens.");
            }
        }

        var result = include is not null
            ? catalog.Where(q => resolved.Contains(q.Id)).ToList()
            : catalog.Where(q => !resolved.Contains(q.Id)).ToList();

        return (result, null);
    }
}
