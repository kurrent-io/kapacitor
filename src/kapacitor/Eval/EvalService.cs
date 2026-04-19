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
    // DEV-1476: every judge invocation is pinned to a JSON Schema via
    // `claude -p --json-schema`. Without this, judges occasionally emitted
    // free-form text (including harmony-style `<function_calls>` XML as
    // prose) which is unparseable as a verdict. The CLI fulfils the schema
    // through a synthetic `StructuredOutput` tool that costs one extra turn
    // — callers here pass `maxTurns: 2` to accommodate it.
    //
    // Both schemas accept `null` for optional string fields (rather than
    // omitting them) because `--json-schema` enforces `required` — the
    // per-question prompt already instructs the judge to emit explicit
    // nulls, so this matches existing expectations.
    const string VerdictJsonSchema = """
        {"type":"object","properties":{"category":{"type":"string"},"question_id":{"type":"string"},"score":{"type":"integer","minimum":1,"maximum":5},"verdict":{"type":"string","enum":["pass","warn","fail"]},"finding":{"type":"string"},"evidence":{"type":["string","null"]},"recommendation":{"type":["string","null"]},"retain_fact":{"type":["string","null"]}},"required":["category","question_id","score","verdict","finding","evidence","recommendation","retain_fact"],"additionalProperties":false}
        """;

    // maxItems mirrors the prompt's documented caps: at most three
    // strengths/issues and five suggestions. Enforcing this at the schema
    // level keeps retrospectives cheap and prevents the model from padding
    // lists with low-signal bullets just because the schema would let it.
    const string RetrospectiveJsonSchema = """
        {"type":"object","properties":{"overall":{"type":"string"},"strengths":{"type":"array","maxItems":3,"items":{"type":"string"}},"issues":{"type":"array","maxItems":3,"items":{"type":"string"}},"suggestions":{"type":"array","maxItems":5,"items":{"type":"string"}}},"required":["overall","strengths","issues","suggestions"],"additionalProperties":false}
        """;

    // Claude CLI spends one turn calling the synthetic StructuredOutput tool
    // and a second turn emitting the end-of-turn, so eval calls need at
    // least 2. Using 3 gives headroom when the model emits a reasoning
    // block before the tool call on very large retrospective prompts —
    // hitting max-turns here would otherwise leave structured_output
    // unpopulated and the call would surface as a null result.
    const int JudgeMaxTurns = 3;

    /// <summary>
    /// Output of <see cref="PrepareAsync"/> — the shared state threaded
    /// through every <see cref="RunQuestionAsync"/> and finally consumed by
    /// <see cref="FinalizeAsync"/>. All fields are non-null on success.
    /// </summary>
    internal sealed record EvalContext(
        string                                       EvalRunId,
        string                                       EncodedSessionId,
        string                                       SessionId,
        string                                       TraceJson,
        EvalContextResult                            ContextResult,
        IReadOnlyDictionary<string, List<JudgeFact>> KnownFactsByCategory,
        string                                       PromptTemplate
    );

    /// <summary>
    /// Runs the full eval pipeline for <paramref name="sessionId"/>:
    /// fetches the compacted trace, runs judge questions sequentially
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
            string                          baseUrl,
            HttpClient                      httpClient,
            string                          sessionId,
            string                          model,
            bool                            chain,
            int?                            thresholdBytes,
            IEvalObserver                   observer,
            CancellationToken               ct        = default,
            string?                         evalRunId = null,
            IReadOnlyList<EvalQuestionDto>? questions = null
        ) {
        // Wrap the caller-supplied observer so any throw from a callback
        // (e.g. SignalR push failures in the daemon) is caught and logged
        // without aborting the eval — IEvalObserver documents this guarantee.
        observer = new SafeObserver(observer);

        try {
            questions ??= await EvalQuestionCatalogClient.FetchAsync(baseUrl, httpClient, ct);
            if (questions is null || questions.Count == 0) {
                observer.OnFailed("failed to load eval question catalog from server");
                return null;
            }

            var ctx = await PrepareAsync(baseUrl, httpClient, sessionId, questions, chain, thresholdBytes, observer, ct, model, evalRunId);
            if (ctx is null) return null;

            var verdicts = new List<EvalQuestionVerdict>();
            for (var i = 0; i < questions.Count; i++) {
                var verdict = await RunQuestionAsync(ctx, httpClient, baseUrl, questions[i], model, i + 1, questions.Count, observer, ct);
                if (verdict is not null) verdicts.Add(verdict);
            }

            return await FinalizeAsync(ctx, httpClient, baseUrl, verdicts, model, questions, observer, ct);
        } catch (OperationCanceledException) {
            // Honour the contract that observers always see OnFinished or
            // OnFailed — cancellation isn't an exception path consumers
            // should have to special-case.
            observer.OnFailed("cancelled");

            return null;
        }
    }

    // ── Phase 1: Prepare ───────────────────────────────────────────────────

    public static async Task<EvalContext?> PrepareAsync(
            string                         baseUrl,
            HttpClient                     httpClient,
            string                         sessionId,
            IReadOnlyList<EvalQuestionDto> questions,
            bool                           chain,
            int?                           thresholdBytes,
            IEvalObserver                  observer,
            CancellationToken              ct,
            string                         model,
            string?                        evalRunId = null
        ) {
        evalRunId ??= Guid.NewGuid().ToString();

        // Session IDs are typically UUIDs but meta-session slugs are free-form
        // user input; escape once and reuse for every session-scoped URL so
        // reserved path characters don't corrupt the request.
        var encodedSessionId = Uri.EscapeDataString(sessionId);

        // 1. Fetch the compacted eval context.
        string             traceJson;
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

        observer.OnStarted(evalRunId, context.SessionId, model, questions.Count);

        observer.OnContextFetched(
            context.Trace.Count,
            traceJson.Length,
            context.Compaction.ToolResultsTotal,
            context.Compaction.ToolResultsTruncated,
            context.Compaction.BytesSaved
        );

        // 2. Fetch retained judge facts per category to inject as known patterns.
        var knownFactsByCategory = await FetchAllJudgeFactsAsync(httpClient, baseUrl, encodedSessionId, questions, observer, ct);
        var promptTemplate       = EmbeddedResources.Load("prompt-eval-question.txt");

        return new EvalContext(
            EvalRunId:            evalRunId,
            EncodedSessionId:     encodedSessionId,
            SessionId:            context.SessionId,
            TraceJson:            traceJson,
            ContextResult:        context,
            KnownFactsByCategory: knownFactsByCategory,
            PromptTemplate:       promptTemplate
        );
    }

    // ── Phase 2: RunQuestion ───────────────────────────────────────────────

    public static async Task<EvalQuestionVerdict?> RunQuestionAsync(
            EvalContext        ctx,
            HttpClient         httpClient,
            string             baseUrl,
            EvalQuestionDto    question,
            string             model,
            int                index,
            int                total,
            IEvalObserver      observer,
            CancellationToken  ct
        ) {
        ct.ThrowIfCancellationRequested();
        observer.OnQuestionStarted(index, total, question.Category, question.Id);

        var patterns = FormatKnownPatterns(ctx.KnownFactsByCategory.GetValueOrDefault(question.Category, []));
        var prompt   = BuildQuestionPrompt(ctx.PromptTemplate, ctx.SessionId, ctx.EvalRunId,
            question, ctx.TraceJson, patterns);

        // Capture ClaudeCliRunner diagnostics (exit code, stdout preview)
        // so a null result gets reported with *why* it was null — those
        // lines are the only signal about API errors or max-turn failures,
        // and daemon observers log OnInfo at Debug level where they vanish.
        var diagnostics = new List<string>();
        var result = await ClaudeCliRunner.RunAsync(
            prompt,
            TimeSpan.FromMinutes(5),
            msg => { diagnostics.Add(msg); observer.OnInfo($"  {msg}"); },
            model: model,
            maxTurns: JudgeMaxTurns,
            // Prompts embed the full compacted trace and can be hundreds
            // of KB — well past Windows' 32K argv limit. Stream via stdin.
            promptViaStdin: true,
            jsonSchema: VerdictJsonSchema,
            ct: ct
        );

        if (result is null) {
            var reason = diagnostics.Count == 0
                ? "null claude result"
                : $"null claude result; {string.Join(" | ", diagnostics.Select(d => Truncate(d, 300)))}";
            observer.OnQuestionFailed(index, total, question.Category, question.Id, reason);

            return null;
        }

        var verdict = ParseVerdict(
            result.Result,
            question,
            onContractViolation: msg => observer.OnInfo($"  {question.Category}/{question.Id}: {msg}")
        );
        if (verdict is null) {
            observer.OnQuestionFailed(index, total, question.Category, question.Id,
                $"verdict JSON could not be parsed; raw response: {Truncate(result.Result, 500)}");

            return null;
        }

        observer.OnQuestionCompleted(index, total, verdict, result.InputTokens, result.OutputTokens);

        // If the judge emitted a retain_fact, persist it for future evals.
        if (ExtractRetainFact(result.Result) is { } retainedFact) {
            if (await PostJudgeFactAsync(httpClient, baseUrl, ctx.EncodedSessionId, question.Category,
                    retainedFact, ctx.EvalRunId, observer, ct)) {
                observer.OnFactRetained(question.Category, retainedFact);
            }
        }

        return verdict;
    }

    // ── Phase 3: Finalize ──────────────────────────────────────────────────

    public static async Task<SessionEvalCompletedPayload?> FinalizeAsync(
            EvalContext                        ctx,
            HttpClient                         httpClient,
            string                             baseUrl,
            IReadOnlyList<EvalQuestionVerdict> verdicts,
            string                             model,
            IReadOnlyList<EvalQuestionDto>     questions,
            IEvalObserver                      observer,
            CancellationToken                  ct
        ) {
        if (verdicts.Count == 0) {
            observer.OnFailed("all judge invocations failed");

            return null;
        }

        // 4. Aggregate per-category + overall scores.
        var aggregate = Aggregate(verdicts, ctx.EvalRunId, model, questions);

        // 5. Synthesise a retrospective from the per-question verdicts. Non-fatal:
        //    the verdicts are the persistence contract, a failed synthesis
        //    just leaves Retrospective=null on the payload.
        var retrospective = await RunRetrospectiveAsync(
            evalRunId:            ctx.EvalRunId,
            sessionId:            ctx.EncodedSessionId,
            model:                model,
            traceJson:            ctx.TraceJson,
            aggregate:            aggregate,
            verdicts:             verdicts,
            knownFactsByCategory: ctx.KnownFactsByCategory,
            observer:             observer,
            ct:                   ct
        );
        aggregate = aggregate with { Retrospective = retrospective };

        // 6. Persist the aggregate to the server.
        var postUrl     = $"{baseUrl}/api/sessions/{ctx.EncodedSessionId}/evals";
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
            string          template,
            string          sessionId,
            string          evalRunId,
            EvalQuestionDto question,
            string          traceJson,
            string          knownPatterns
        ) =>
        template
            .Replace("{SESSION_ID}",     sessionId)
            .Replace("{EVAL_RUN_ID}",    evalRunId)
            .Replace("{CATEGORY}",       question.Category)
            .Replace("{QUESTION_ID}",    question.Id)
            .Replace("{QUESTION_TEXT}",  question.Prompt)
            .Replace("{TRACE_JSON}",     traceJson)
            .Replace("{KNOWN_PATTERNS}", knownPatterns);

    static readonly string RetrospectivePromptTemplate =
        EmbeddedResources.Load("prompt-eval-retrospective.txt");

    /// <summary>
    /// Builds the retrospective synthesis prompt from the four placeholders the
    /// judge needs: session metadata, per-question verdicts, retained
    /// patterns from prior evals in this repo, and the compacted trace. The
    /// prompt file itself is loaded once at startup.
    /// </summary>
    public static string BuildRetrospectivePrompt(
            string sessionMeta,
            string verdictsJson,
            string knownPatterns,
            string traceJson
        ) =>
        RetrospectivePromptTemplate
            .Replace("{SESSION_META}",   sessionMeta)
            .Replace("{VERDICTS_JSON}",  verdictsJson)
            .Replace("{KNOWN_PATTERNS}", knownPatterns)
            .Replace("{TRACE_JSON}",     traceJson);

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
    ///
    /// <para>
    /// The recommendation contract ("required when score &lt; 4, optional
    /// at 4, null at 5") can't be expressed in the JSON schema we pass to
    /// <c>claude --json-schema</c> — Anthropic's tool input_schema rejects
    /// top-level <c>oneOf</c>/<c>allOf</c>/<c>anyOf</c>, so conditional
    /// requirements aren't representable. Enforced here instead: score=5
    /// verdicts always have their recommendation nulled (the contract
    /// says there shouldn't be one, and a score-5 recommendation is
    /// meaningless anyway). Score &lt; 4 with a missing recommendation is
    /// flagged via <paramref name="onContractViolation"/> but still
    /// accepted — dropping a valid score/finding/evidence because the
    /// recommendation is missing is a worse outcome than a partial verdict.
    /// </para>
    /// </summary>
    public static EvalQuestionVerdict? ParseVerdict(
            string          rawResponse,
            EvalQuestionDto question,
            Action<string>? onContractViolation = null
        ) {
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

        var normalisedRecommendation = parsed.Recommendation?.Trim();
        if (string.IsNullOrEmpty(normalisedRecommendation)) normalisedRecommendation = null;

        // Contract: null recommendation at score=5, concrete recommendation
        // at score<4. Normalise score=5 unconditionally; surface score<4
        // violations but accept the verdict anyway.
        if (parsed.Score == 5 && normalisedRecommendation is not null) {
            onContractViolation?.Invoke($"score 5 verdict included a recommendation — nulling per contract");
            normalisedRecommendation = null;
        }

        if (parsed.Score < 4 && normalisedRecommendation is null) {
            onContractViolation?.Invoke($"score {parsed.Score} verdict missing recommendation — accepting partial verdict");
        }

        return parsed with {
            Category       = question.Category,
            QuestionId     = question.Id,
            Verdict        = VerdictForScore(parsed.Score),
            Recommendation = normalisedRecommendation
        };
    }

    /// <summary>
    /// Parses the retrospective synthesis response. Tolerant of markdown code
    /// fences. Returns null on empty/whitespace input, malformed JSON, or a
    /// <c>null</c> JSON literal. Synthesis failure is non-fatal — the caller
    /// leaves <see cref="SessionEvalCompletedPayload.Retrospective"/> as null.
    /// </summary>
    public static EvalRetrospective? ParseRetrospective(string rawResponse) {
        var json = StripCodeFences(rawResponse.Trim());
        if (string.IsNullOrWhiteSpace(json)) return null;

        try {
            return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.EvalRetrospective);
        } catch (JsonException) {
            return null;
        }
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

    /// <summary>
    /// Prepares untrusted model or subprocess output for embedding in a
    /// single-line log/observer reason: escapes C0 control chars (so newlines
    /// can't fake multi-line log entries or inject ANSI/log-structure), then
    /// truncates to <paramref name="max"/> with a remainder marker. Both steps
    /// matter — sanitising without truncating lets model output dominate the
    /// log, truncating without sanitising lets a single \n split the reason
    /// into what looks like two log entries.
    /// </summary>
    internal static string Truncate(string text, int max) {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) {
            sb.Append(c switch {
                '\n'             => "\\n",
                '\r'             => "\\r",
                '\t'             => "\\t",
                < ' ' or '\x7f'  => "?",
                _                => c.ToString()
            });
        }
        var sanitised = sb.ToString();
        return sanitised.Length <= max ? sanitised : sanitised[..max] + $"… ({sanitised.Length - max} more chars)";
    }

    // ── Aggregation ────────────────────────────────────────────────────────

    public static SessionEvalCompletedPayload Aggregate(
            IReadOnlyList<EvalQuestionVerdict> verdicts,
            string                             evalRunId,
            string                             model,
            IReadOnlyList<EvalQuestionDto>     questions
        ) {
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
            .OrderBy(c => CategoryOrderFromTaxonomy(c.Name, questions))
            .ToList();

        var overall = byCategory.Count > 0
            ? (int)Math.Round(byCategory.Average(c => c.Score))
            : 0;

        var summary = $"Evaluated {verdicts.Count}/{questions.Count} questions "
            + $"across {byCategory.Count} categories. Overall: {overall}/5 ({VerdictForScore(overall)}).";

        return new SessionEvalCompletedPayload {
            EvalRunId    = evalRunId,
            JudgeModel   = model,
            Categories   = byCategory,
            OverallScore = overall,
            Summary      = summary
        };
    }

    static int CategoryOrderFromTaxonomy(string category, IReadOnlyList<EvalQuestionDto> questions) {
        var idx  = 0;
        var seen = new HashSet<string>();
        foreach (var q in questions) {
            if (seen.Add(q.Category)) {
                if (q.Category == category) return idx;
                idx++;
            }
        }
        return 99;
    }

    public static string VerdictForScore(int score) => score switch {
        >= 4 => "pass",
        >= 2 => "warn",
        _    => "fail"
    };

    // ── Retrospective synthesis ────────────────────────────────────────────

    /// <summary>
    /// Synthesises a retrospective from the per-question verdicts using one
    /// extra judge invocation with the same model and timeout as a
    /// per-question call. Failure is non-fatal — the caller just leaves
    /// <see cref="SessionEvalCompletedPayload.Retrospective"/> as null and
    /// persists the verdicts regardless. Only
    /// <see cref="OperationCanceledException"/> propagates so upstream
    /// cancellation still cancels the eval run.
    /// </summary>
    static async Task<EvalRetrospective?> RunRetrospectiveAsync(
            string                                      evalRunId,
            string                                      sessionId,
            string                                      model,
            string                                      traceJson,
            SessionEvalCompletedPayload                 aggregate,
            IReadOnlyList<EvalQuestionVerdict>          verdicts,
            IReadOnlyDictionary<string, List<JudgeFact>> knownFactsByCategory,
            IEvalObserver                               observer,
            CancellationToken                           ct
        ) {
        // Check cancellation before emitting OnRetrospectiveStarted so a
        // shutdown between the per-question loop and retrospective doesn't
        // leave observers seeing a started-never-finished synthesis event.
        ct.ThrowIfCancellationRequested();

        observer.OnRetrospectiveStarted();

        var sessionMeta   = $"session-id: {sessionId}\nrun-id: {evalRunId}\nmodel: {model}\noverall-score: {aggregate.OverallScore}/5";
        var verdictsJson  = JsonSerializer.Serialize(verdicts, KapacitorJsonContext.Default.IReadOnlyListEvalQuestionVerdict);
        var knownPatterns = FormatKnownPatternsAllCategories(knownFactsByCategory);
        var prompt        = BuildRetrospectivePrompt(sessionMeta, verdictsJson, knownPatterns, traceJson);

        try {
            var result = await ClaudeCliRunner.RunAsync(
                prompt,
                TimeSpan.FromMinutes(5),
                msg => observer.OnInfo($"  {msg}"),
                model:          model,
                maxTurns:       JudgeMaxTurns,
                // Prompt embeds full compacted trace + verdicts; likely >32K on Windows.
                promptViaStdin: true,
                jsonSchema:     RetrospectiveJsonSchema,
                ct:             ct
            );

            if (result is null) {
                observer.OnRetrospectiveFailed("claude returned null (timeout, non-zero exit, or unparseable response)");

                return null;
            }

            var retrospective = ParseRetrospective(result.Result);
            if (retrospective is null) {
                observer.OnRetrospectiveFailed($"retrospective response did not parse as expected JSON shape; raw response: {Truncate(result.Result, 500)}");

                return null;
            }

            observer.OnRetrospectiveCompleted(retrospective);

            return retrospective;
        } catch (OperationCanceledException) {
            // Upstream cancellation must continue to cancel — don't swallow.
            throw;
        } catch (Exception ex) {
            observer.OnRetrospectiveFailed(ex.Message);

            return null;
        }
    }

    /// <summary>
    /// Formats retained facts across all categories as a bulleted block for
    /// the retrospective prompt's <c>{KNOWN_PATTERNS}</c> placeholder.
    /// Produces an explicit empty-state string when nothing has been retained
    /// yet so the prompt section doesn't read as a broken substitution.
    /// </summary>
    static string FormatKnownPatternsAllCategories(IReadOnlyDictionary<string, List<JudgeFact>> facts) {
        if (facts.Count == 0 || facts.Values.All(v => v.Count == 0)) {
            return "(no patterns retained from prior evals in this repo)";
        }

        var sb = new StringBuilder();
        foreach (var (category, list) in facts.OrderBy(kv => kv.Key)) {
            if (list.Count == 0) continue;
            sb.AppendLine($"{category}:");
            foreach (var fact in list) sb.AppendLine($"- {fact.Fact}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ── Judge-facts HTTP ───────────────────────────────────────────────────

    static async Task<Dictionary<string, List<JudgeFact>>> FetchAllJudgeFactsAsync(
            HttpClient                     httpClient,
            string                         baseUrl,
            string                         encodedSessionId,
            IReadOnlyList<EvalQuestionDto> questions,
            IEvalObserver                  observer,
            CancellationToken              ct
        ) {
        var result = new Dictionary<string, List<JudgeFact>>();

        foreach (var category in questions.Select(q => q.Category).Distinct(StringComparer.Ordinal)) {
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

        public void OnRetrospectiveStarted() =>
            Safe(inner.OnRetrospectiveStarted, nameof(OnRetrospectiveStarted));

        public void OnRetrospectiveCompleted(EvalRetrospective retrospective) =>
            Safe(() => inner.OnRetrospectiveCompleted(retrospective), nameof(OnRetrospectiveCompleted));

        public void OnRetrospectiveFailed(string reason) =>
            Safe(() => inner.OnRetrospectiveFailed(reason), nameof(OnRetrospectiveFailed));

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
