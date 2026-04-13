using System.Text;
using System.Text.Json;

namespace kapacitor.Eval;

/// <summary>
/// Core orchestration for an LLM-as-judge eval run. Consumed by the CLI
/// (<c>kapacitor eval</c>) and — per DEV-1440 milestone 2 — by the daemon
/// when the dashboard dispatches an evaluation. All progress is reported
/// through <see cref="IEvalObserver"/> so the two host environments can
/// render it differently (stderr logs vs SignalR events) without the
/// service caring.
/// </summary>
internal static class EvalService {
    /// <summary>
    /// Runs the full eval pipeline for <paramref name="sessionId"/>:
    /// fetches the compacted trace, runs 13 judge questions sequentially
    /// against the <paramref name="model"/>, aggregates per-category and
    /// overall scores, persists the result back to the server, and
    /// optionally retains any cross-cutting patterns the judges surfaced.
    ///
    /// <para>
    /// Returns the aggregated payload on success, or <c>null</c> if the
    /// run failed before producing a meaningful aggregate. Observers
    /// receive a final <see cref="IEvalObserver.OnFinished"/> or
    /// <see cref="IEvalObserver.OnFailed"/> either way.
    /// </para>
    /// </summary>
    public static async Task<SessionEvalCompletedPayload?> RunAsync(
            string            baseUrl,
            HttpClient        httpClient,
            string            sessionId,
            string            model,
            bool              chain,
            int?              thresholdBytes,
            IEvalObserver     observer,
            CancellationToken ct = default
        ) {
        // Wrap the caller-supplied observer so any throw from a callback
        // (e.g. SignalR push failures in the daemon) is caught and logged
        // without aborting the eval — IEvalObserver documents this guarantee.
        observer = new SafeObserver(observer);

        var evalRunId = Guid.NewGuid().ToString();

        // Session IDs are typically UUIDs but meta-session slugs are free-form
        // user input; escape once and reuse for every session-scoped URL so
        // reserved path characters don't corrupt the request.
        var encodedSessionId = Uri.EscapeDataString(sessionId);

        try {
            return await RunInnerAsync(baseUrl, httpClient, encodedSessionId, evalRunId, model, chain, thresholdBytes, observer, ct);
        } catch (OperationCanceledException) {
            // Honour the contract that observers always see OnFinished or
            // OnFailed — cancellation isn't an exception path consumers
            // should have to special-case.
            observer.OnFailed("cancelled");

            return null;
        }
    }

    static async Task<SessionEvalCompletedPayload?> RunInnerAsync(
            string            baseUrl,
            HttpClient        httpClient,
            string            encodedSessionId,
            string            evalRunId,
            string            model,
            bool              chain,
            int?              thresholdBytes,
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        // 1. Fetch the compacted eval context.
        string              traceJson;
        EvalContextResult? context;

        try {
            var url = $"{baseUrl}/api/sessions/{encodedSessionId}/eval-context"
                + (chain ? "?chain=true" : "")
                + (thresholdBytes is { } t ? (chain ? "&" : "?") + $"threshold={t}" : "");

            using var resp = await httpClient.GetWithRetryAsync(url, ct: ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) {
                // Detect 401 directly rather than going through
                // HttpClientExtensions.HandleUnauthorizedAsync — that helper
                // writes to stderr, which would duplicate output for CLI
                // callers and add noise for daemon callers that route via
                // SignalR. The observer is the single reporting channel.
                observer.OnFailed("authentication failed — run 'kapacitor login' to re-authenticate");

                return null;
            }

            if (!resp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to fetch eval context: HTTP {(int)resp.StatusCode}");

                return null;
            }

            traceJson = await resp.Content.ReadAsStringAsync(ct);
            context   = JsonSerializer.Deserialize(traceJson, KapacitorJsonContext.Default.EvalContextResult);
        } catch (HttpRequestException ex) {
            observer.OnFailed($"server unreachable: {ex.Message}");

            return null;
        }

        if (context is null) {
            observer.OnFailed("eval context response was not valid JSON");

            return null;
        }

        if (context.Trace.Count == 0) {
            observer.OnFailed("session has no recorded activity — nothing to evaluate");

            return null;
        }

        // OnStarted before OnContextFetched so the user-facing log reads
        // "Evaluating session..." then "Fetched...", matching the pre-refactor
        // CLI output order.
        observer.OnStarted(evalRunId, context.SessionId, model, EvalQuestions.All.Length);
        observer.OnContextFetched(
            context.Trace.Count,
            traceJson.Length,
            context.Compaction.ToolResultsTotal,
            context.Compaction.ToolResultsTruncated,
            context.Compaction.BytesSaved
        );

        // 2. Fetch retained judge facts per category to inject as known
        //    patterns. Per-category failures don't abort the run.
        var knownFactsByCategory = await FetchAllJudgeFactsAsync(httpClient, baseUrl, encodedSessionId, observer, ct);

        // 3. Run each question in sequence.
        var promptTemplate = EmbeddedResources.Load("prompt-eval-question.txt");
        var verdicts       = new List<EvalQuestionVerdict>();

        for (var i = 0; i < EvalQuestions.All.Length; i++) {
            ct.ThrowIfCancellationRequested();

            var q = EvalQuestions.All[i];
            observer.OnQuestionStarted(i + 1, EvalQuestions.All.Length, q.Category, q.Id);

            var patterns = FormatKnownPatterns(knownFactsByCategory.GetValueOrDefault(q.Category, []));
            var prompt   = BuildQuestionPrompt(promptTemplate, context.SessionId, evalRunId, q, traceJson, patterns);

            var result = await ClaudeCliRunner.RunAsync(
                prompt,
                TimeSpan.FromMinutes(5),
                msg => observer.OnInfo($"  {msg}"),
                model: model,
                maxTurns: 1,
                // Prompts embed the full compacted trace and can be hundreds
                // of KB — well past Windows' 32K argv limit. Stream via stdin.
                promptViaStdin: true
            );

            if (result is null) {
                observer.OnQuestionFailed(i + 1, EvalQuestions.All.Length, q.Category, q.Id, "null claude result");

                continue;
            }

            var verdict = ParseVerdict(result.Result, q);
            if (verdict is null) {
                observer.OnQuestionFailed(i + 1, EvalQuestions.All.Length, q.Category, q.Id, "verdict JSON could not be parsed");

                continue;
            }

            verdicts.Add(verdict);
            observer.OnQuestionCompleted(i + 1, EvalQuestions.All.Length, verdict, result.InputTokens, result.OutputTokens);

            // If the judge emitted a retain_fact, persist it for future evals.
            if (ExtractRetainFact(result.Result) is { } retainedFact) {
                if (await PostJudgeFactAsync(httpClient, baseUrl, encodedSessionId, q.Category, retainedFact, evalRunId, observer, ct)) {
                    observer.OnFactRetained(q.Category, retainedFact);
                }
            }
        }

        if (verdicts.Count == 0) {
            observer.OnFailed("all judge invocations failed");

            return null;
        }

        // 4. Aggregate per-category + overall scores.
        var aggregate = Aggregate(verdicts, evalRunId, model);

        // 5. Persist the aggregate to the server.
        var postUrl     = $"{baseUrl}/api/sessions/{encodedSessionId}/evals";
        var payloadJson = JsonSerializer.Serialize(aggregate, KapacitorJsonContext.Default.SessionEvalCompletedPayload);
        using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            using var postResp = await httpClient.PostWithRetryAsync(postUrl, httpContent, ct: ct);
            if (!postResp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to persist eval result: HTTP {(int)postResp.StatusCode}");

                return null;
            }
        } catch (HttpRequestException ex) {
            observer.OnFailed($"server unreachable for POST: {ex.Message}");

            return null;
        }

        observer.OnFinished(aggregate);

        return aggregate;
    }

    // ── Prompt construction ────────────────────────────────────────────────

    public static string BuildQuestionPrompt(
            string                 template,
            string                 sessionId,
            string                 evalRunId,
            EvalQuestions.Question question,
            string                 traceJson,
            string                 knownPatterns
        ) =>
        template
            .Replace("{SESSION_ID}",     sessionId)
            .Replace("{EVAL_RUN_ID}",    evalRunId)
            .Replace("{CATEGORY}",       question.Category)
            .Replace("{QUESTION_ID}",    question.Id)
            .Replace("{QUESTION_TEXT}",  question.Text)
            .Replace("{TRACE_JSON}",     traceJson)
            .Replace("{KNOWN_PATTERNS}", knownPatterns);

    /// <summary>
    /// Formats a per-category list of retained facts as a bulleted block for
    /// injection into the judge prompt. Empty list renders an explicit
    /// "(none yet)" marker so the section reads naturally.
    /// </summary>
    public static string FormatKnownPatterns(List<JudgeFact> facts) {
        if (facts.Count == 0) {
            return "_(no patterns retained for this category yet)_";
        }

        var sb = new StringBuilder();
        foreach (var f in facts) {
            sb.AppendLine($"- {f.Fact}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Verdict parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Parses a judge's JSON verdict and normalizes it against the schema
    /// contract before the server ever sees it. Tolerant of markdown code
    /// fences. Returns null if the response is unparseable or the score is
    /// out of the 1..5 range.
    ///
    /// <para>
    /// Category/question_id are overridden to match what we asked about
    /// (judges sometimes hallucinate ids) and the verdict string is always
    /// derived from the score — the prompt documents the mapping, so
    /// trusting the score over the judge-supplied verdict eliminates a
    /// whole class of mild hallucinations without discarding useful data.
    /// </para>
    /// </summary>
    public static EvalQuestionVerdict? ParseVerdict(string rawResponse, EvalQuestions.Question question) {
        var json = StripCodeFences(rawResponse.Trim());

        EvalQuestionVerdict? parsed;
        try {
            parsed = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.EvalQuestionVerdict);
        } catch (JsonException) {
            return null;
        }

        if (parsed is null) return null;

        if (parsed.Score is < 1 or > 5) {
            return null;
        }

        return parsed with {
            Category   = question.Category,
            QuestionId = question.Id,
            Verdict    = VerdictForScore(parsed.Score)
        };
    }

    /// <summary>
    /// Extracts the optional <c>retain_fact</c> string from a raw judge
    /// response. Returns null when absent, explicitly null, empty, or when
    /// the response isn't parseable JSON. Independent of
    /// <see cref="ParseVerdict"/> so the retained-fact plumbing doesn't
    /// depend on verdict parsing succeeding.
    /// </summary>
    public static string? ExtractRetainFact(string rawResponse) {
        var json = StripCodeFences(rawResponse.Trim());

        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("retain_fact", out var prop)) {
                return null;
            }

            if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                return null;
            }

            if (prop.ValueKind != JsonValueKind.String) {
                return null;
            }

            var text = prop.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        } catch (JsonException) {
            return null;
        }
    }

    static string StripCodeFences(string text) {
        if (!text.StartsWith("```")) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) {
            text = text[(firstNewline + 1)..];
        }

        if (text.EndsWith("```")) {
            text = text[..^3].TrimEnd();
        }

        return text.Trim();
    }

    // ── Aggregation ────────────────────────────────────────────────────────

    public static SessionEvalCompletedPayload Aggregate(List<EvalQuestionVerdict> verdicts, string evalRunId, string model) {
        var byCategory = verdicts
            .GroupBy(v => v.Category)
            .Select(g => {
                var avg = (int)Math.Round(g.Average(v => v.Score));

                return new EvalCategoryResult {
                    Name      = g.Key,
                    Score     = avg,
                    Verdict   = VerdictForScore(avg),
                    Questions = g.ToList()
                };
            })
            .OrderBy(c => EvalQuestions.CategoryOrder(c.Name))
            .ToList();

        var overall = byCategory.Count > 0
            ? (int)Math.Round(byCategory.Average(c => c.Score))
            : 0;

        var summary = $"Evaluated {verdicts.Count}/{EvalQuestions.All.Length} questions "
            + $"across {byCategory.Count} categories. Overall: {overall}/5 ({VerdictForScore(overall)}).";

        return new SessionEvalCompletedPayload {
            EvalRunId    = evalRunId,
            JudgeModel   = model,
            Categories   = byCategory,
            OverallScore = overall,
            Summary      = summary
        };
    }

    public static string VerdictForScore(int score) => score switch {
        >= 4 => "pass",
        >= 2 => "warn",
        _    => "fail"
    };

    // ── Judge-facts HTTP ───────────────────────────────────────────────────

    static async Task<Dictionary<string, List<JudgeFact>>> FetchAllJudgeFactsAsync(
            HttpClient        httpClient,
            string            baseUrl,
            string            encodedSessionId,
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        var result = new Dictionary<string, List<JudgeFact>>();

        foreach (var category in EvalQuestions.Categories) {
            try {
                using var resp = await httpClient.GetWithRetryAsync(
                    $"{baseUrl}/api/sessions/{encodedSessionId}/judge-facts?category={Uri.EscapeDataString(category)}",
                    ct: ct
                );
                if (!resp.IsSuccessStatusCode) {
                    observer.OnInfo($"Failed to fetch judge facts for {category}: HTTP {(int)resp.StatusCode}");

                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var list = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListJudgeFact) ?? [];
                result[category] = list;
                observer.OnInfo($"Loaded {list.Count} retained facts for category {category}");
            } catch (HttpRequestException ex) {
                observer.OnInfo($"Could not load judge facts for {category}: {ex.Message}");
            }
        }

        return result;
    }

    static async Task<bool> PostJudgeFactAsync(
            HttpClient        httpClient,
            string            baseUrl,
            string            encodedSessionId,
            string            category,
            string            fact,
            string            evalRunId,
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        var payload = new JudgeFactPayload {
            Category        = category,
            Fact            = fact,
            SourceEvalRunId = evalRunId
        };

        var payloadJson = JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.JudgeFactPayload);
        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            using var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/api/sessions/{encodedSessionId}/judge-facts", content, ct: ct);
            if (!resp.IsSuccessStatusCode) {
                observer.OnInfo($"failed to retain fact for category {category}: HTTP {(int)resp.StatusCode}");

                return false;
            }

            return true;
        } catch (HttpRequestException ex) {
            observer.OnInfo($"failed to retain fact for category {category}: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// Wraps an <see cref="IEvalObserver"/> so each callback's exception is
    /// caught and logged to stderr, rather than aborting the eval. Honours
    /// the observer-throw guarantee documented on
    /// <see cref="IEvalObserver"/>. The fallback log path is deliberately
    /// minimal — if even <c>Console.Error</c> throws (extremely unlikely
    /// outside CI sandboxes), we swallow that too rather than risk
    /// corrupting eval state for a logging side effect.
    /// </summary>
    sealed class SafeObserver(IEvalObserver inner) : IEvalObserver {
        public void OnInfo(string message) => Safe(() => inner.OnInfo(message), nameof(OnInfo));

        public void OnStarted(string evalRunId, string sessionId, string judgeModel, int totalQuestions) =>
            Safe(() => inner.OnStarted(evalRunId, sessionId, judgeModel, totalQuestions), nameof(OnStarted));

        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
            Safe(() => inner.OnContextFetched(traceEntries, traceChars, toolResultsTotal, toolResultsTruncated, bytesSaved), nameof(OnContextFetched));

        public void OnQuestionStarted(int index, int total, string category, string questionId) =>
            Safe(() => inner.OnQuestionStarted(index, total, category, questionId), nameof(OnQuestionStarted));

        public void OnQuestionCompleted(int index, int total, EvalQuestionVerdict verdict, long inputTokens, long outputTokens) =>
            Safe(() => inner.OnQuestionCompleted(index, total, verdict, inputTokens, outputTokens), nameof(OnQuestionCompleted));

        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) =>
            Safe(() => inner.OnQuestionFailed(index, total, category, questionId, reason), nameof(OnQuestionFailed));

        public void OnFactRetained(string category, string fact) =>
            Safe(() => inner.OnFactRetained(category, fact), nameof(OnFactRetained));

        public void OnFinished(SessionEvalCompletedPayload aggregate) =>
            Safe(() => inner.OnFinished(aggregate), nameof(OnFinished));

        public void OnFailed(string reason) =>
            Safe(() => inner.OnFailed(reason), nameof(OnFailed));

        static void Safe(Action notify, string callbackName) {
            try {
                notify();
            } catch (Exception ex) {
                try {
                    Console.Error.WriteLine($"[eval] observer {callbackName} threw: {ex.GetType().Name}: {ex.Message}");
                } catch {
                    // Don't propagate — the eval pipeline mustn't fail because
                    // the failure-log channel itself failed.
                }
            }
        }
    }
}
