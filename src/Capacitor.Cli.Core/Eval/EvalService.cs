using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Eval;

/// <summary>
/// Core orchestration for an LLM-as-judge eval run. Consumed by the CLI
/// (<c>kcap eval</c>) and — per DEV-1440 milestone 2 — by the daemon
/// when the dashboard dispatches an evaluation. All progress is reported
/// through <see cref="IEvalObserver"/> so the two host environments can
/// render it differently (stderr logs vs SignalR events) without the
/// service caring.
/// </summary>
public static class EvalService {
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
        {"type":"object","properties":{"category":{"type":"string"},"question_id":{"type":"string"},"score":{"type":"integer","minimum":1,"maximum":5},"verdict":{"type":"string","enum":["pass","warn","fail"]},"finding":{"type":"string"},"evidence":{"type":["string","null"]},"recommendation":{"type":["string","null"]},"retain_fact":{"type":["string","object","null"],"properties":{"fact":{"type":"string"},"applies_to_vendors":{"type":"array","items":{"type":"string"},"maxItems":16},"applies_to_session_kinds":{"type":"array","items":{"type":"string"},"maxItems":16}},"required":["fact"],"additionalProperties":false}},"required":["category","question_id","score","verdict","finding","evidence","recommendation","retain_fact"],"additionalProperties":false}
        """;

    // maxItems mirrors the prompt's documented caps: at most three
    // strengths/issues and five suggestions. Enforcing this at the schema
    // level keeps retrospectives cheap and prevents the model from padding
    // lists with low-signal bullets just because the schema would let it.
    //
    // suggestions.items is an object with {text, audience} — NOT a
    // bare string. The previous string-items schema forced the model to
    // ignore the prompt's {text, audience} instruction, which meant every
    // suggestion landed as audience="human" and no agent_guidance was ever
    // produced by CLI-driven evals.
    const string RetrospectiveJsonSchema = """
        {"type":"object","properties":{"overall":{"type":"string"},"strengths":{"type":"array","maxItems":3,"items":{"type":"string"}},"issues":{"type":"array","maxItems":3,"items":{"type":"string"}},"suggestions":{"type":"array","maxItems":5,"items":{"type":"object","properties":{"text":{"type":"string"},"audience":{"type":"string","enum":["agent","human"]}},"required":["text","audience"],"additionalProperties":false}}},"required":["overall","strengths","issues","suggestions"],"additionalProperties":false}
        """;

    /// <summary>
    /// Exposes <see cref="RetrospectiveJsonSchema"/> for test-time schema
    /// validation. Production callers reference the constant directly.
    /// </summary>
    internal static string GetRetrospectiveJsonSchema() => RetrospectiveJsonSchema;

    // Claude CLI spends one turn calling the synthetic StructuredOutput tool
    // and a second turn emitting the end-of-turn, so eval calls need at
    // least 2. Using 3 gives headroom when the model emits a reasoning
    // block before the tool call on very large retrospective prompts —
    // hitting max-turns here would otherwise leave structured_output
    // unpopulated and the call would surface as a null result.
    const int JudgeMaxTurns = 3;

    // DEV-1484: the retrospective judge now pulls session details via MCP
    // tools (recap/errors/transcript) instead of reading them from the
    // embedded trace. Each tool call costs a turn, plus one for the final
    // StructuredOutput reply and one end-of-turn. The prompt's "at most 6
    // tool calls" budget collides with reasoning-block turns: assistant
    // tool_use turns and reasoning turns both count, so 6 tool calls can
    // already burn 8-10 turns before StructuredOutput. DEV-1576 raised
    // this from 10 → 15 after real runs were hitting error_max_turns
    // mid-tool-use and producing null results.
    const int RetrospectiveMaxTurns = 15;

    // 15-min wallclock pairs with RetrospectiveMaxTurns=15: gives the judge
    // room for the prompt's 6 MCP tool calls plus structured-output and
    // reasoning headroom even under cold-start claude CLI latency.
    static readonly TimeSpan RetrospectiveTimeout = TimeSpan.FromMinutes(15);

    // DEV-1486: tools-enabled per-question judges reuse the retrospective's
    // MCP tool surface. DEV-1576: original 10 turns / $0.50 was too tight —
    // judges hit error_max_turns mid-investigation and produced null
    // verdicts because StructuredOutput never ran. Bumped to 15 turns /
    // $1.00 to match the retrospective ceiling; the prompt's "at most 6
    // tool calls" still bounds investigation depth.
    const int    ToolsPerQuestionMaxTurns     = 15;
    const double ToolsPerQuestionMaxBudgetUsd = 1.00;

    static readonly TimeSpan ToolsPerQuestionTimeout = TimeSpan.FromMinutes(10);

    // The inline judge MCP server is registered under this key in the
    // --mcp-config we pass to claude; the key becomes the `mcp__<key>__<tool>`
    // prefix claude uses. Named after `kcap mcp judge`'s own serverInfo so it
    // never collides with the plugin-registered `kcap-review` (`kcap mcp
    // review`) server when both load.
    internal const string JudgeMcpServerName = "kcap-judge";

    // Shared between RunRetrospectiveAsync and the tools-enabled per-question
    // branch of RunQuestionAsync. Built from JudgeMcpServerName so the
    // allowlist prefix can never drift from the server key we register —
    // a mismatch would push every judge tool call outside the allowlist and
    // get it permission-blocked. Keeping a single list keeps the call_id →
    // tool_name accounting surface aligned across both judge runs.
    internal static readonly string[] JudgeMcpAllowedTools = [
        $"mcp__{JudgeMcpServerName}__get_session_recap",
        $"mcp__{JudgeMcpServerName}__get_session_errors",
        $"mcp__{JudgeMcpServerName}__get_transcript",
        $"mcp__{JudgeMcpServerName}__get_session_summary",
        $"mcp__{JudgeMcpServerName}__search_session",
        $"mcp__{JudgeMcpServerName}__get_tool_result"
    ];

    /// <summary>
    /// Builds the inline <c>--mcp-config</c> JSON for a session-scoped judge
    /// run: a single <see cref="JudgeMcpServerName"/> server that shells out
    /// to <c>kcap mcp judge --session &lt;id&gt;</c> with <c>KCAP_URL</c>
    /// pinned to the resolved server. Shared by the per-question and
    /// retrospective judge paths so the two never drift. <paramref name="baseUrl"/>
    /// is injected so the child uses the exact server the parent resolved
    /// (which may have come from <c>--server-url</c> and so isn't reachable
    /// via the child's own config lookup).
    /// </summary>
    internal static string BuildJudgeMcpConfig(string commandPath, string sessionId, string baseUrl) =>
        new JsonObject {
            ["mcpServers"] = new JsonObject {
                [JudgeMcpServerName] = new JsonObject {
                    ["command"] = commandPath,
                    ["args"]    = new JsonArray("mcp", "judge", "--session", sessionId),
                    ["env"]     = new JsonObject { ["KCAP_URL"] = baseUrl }
                }
            }
        }.ToJsonString();

    /// <summary>
    /// Resolves the path to the <c>kcap</c> executable used to launch the
    /// session-scoped MCP judge subprocess (<c>kcap mcp judge</c>).
    ///
    /// <para>
    /// The CLI host (<c>kcap eval</c>) is already the <c>kcap</c> binary, so
    /// its <see cref="Environment.ProcessPath"/> is correct. The daemon host,
    /// however, is a separate <c>kcap-daemon</c> binary that has no
    /// <c>mcp judge</c> subcommand — invoking <c>kcap-daemon mcp judge</c>
    /// just tries (and fails, "already running") to start a second daemon, so
    /// the judge's MCP server never comes up and every tool call is dead. The
    /// two binaries ship side-by-side in the same directory, so when the host
    /// is <c>kcap-daemon</c> we remap to the sibling <c>kcap</c> (preserving any
    /// executable extension, e.g. <c>.exe</c>), falling back to the original
    /// path if the sibling isn't found.
    /// </para>
    /// </summary>
    internal static string ResolveJudgeCommandPath(string? processPath, Func<string, bool> fileExists) {
        if (string.IsNullOrEmpty(processPath)) return "kcap";

        var dir = Path.GetDirectoryName(processPath);
        if (dir is not null && Path.GetFileNameWithoutExtension(processPath) == "kcap-daemon") {
            var sibling = Path.Combine(dir, "kcap" + Path.GetExtension(processPath));
            if (fileExists(sibling)) return sibling;
        }

        return processPath;
    }

    static string ResolveJudgeCommandPath() =>
        ResolveJudgeCommandPath(Environment.ProcessPath, File.Exists);

    /// <summary>
    /// Resolves a caller-supplied model alias to the variant we actually
    /// want to dispatch to for a judge call. Today: force the 1M-context
    /// Sonnet variant for any plain <c>sonnet</c> request, because the
    /// embedded compacted trace routinely exceeds the 200K window of the
    /// default Sonnet (see DEV-1474 audit — real traces hit ~278K input
    /// tokens, triggering the CLI's auto-compact and destroying verdicts).
    ///
    /// <para>
    /// Temporary workaround pending DEV-1485 / DEV-1486, which will give
    /// judges session-scoped tool access and drop the embedded trace
    /// entirely. Once those ship, default-Sonnet is enough and this
    /// remap can go away.
    /// </para>
    ///
    /// <para>
    /// Only the two short aliases the dashboard and CLI ship today
    /// (<c>sonnet</c>, <c>claude-sonnet-4-6</c>) are rewritten — every
    /// other alias or explicit full model ID passes through unchanged,
    /// so a caller who knows what they're doing can still opt out by
    /// naming the model exactly.
    /// </para>
    /// </summary>
    public static string JudgeModelFor(string model) => model switch {
        "sonnet"            => "sonnet[1m]",
        "claude-sonnet-4-6" => "claude-sonnet-4-6[1m]",
        _                   => model
    };

    // ── Embedded-trace size gate (size-gated hybrid) ───────────────────────
    //
    // The text-only judge path embeds the whole compacted trace as
    // {TRACE_JSON}. That's cheap and high-fidelity for normal sessions, but
    // very long sessions produce traces that overflow even the judge model's
    // 1M-token context (observed: ~1.28M tokens → HTTP 400 "Prompt is too
    // long", num_turns:1, no verdict). Above this budget PrepareAsync routes
    // EVERY question for the session through the tools path
    // (BuildToolsQuestionPrompt + the MCP judge surface) so the judge fetches
    // only what it needs on demand instead of receiving the trace up front.
    //
    // The budget is a token estimate: the trace is JSON, so ~4 chars/token is
    // a conservative approximation (the real ratio is usually higher, which
    // keeps us on the safe side of the window). Overridable via
    // KCAP_EVAL_TRACE_TOKEN_BUDGET for tuning without shipping a release.
    const int DefaultTraceTokenBudget = 200_000;

    internal static int TraceTokenBudget() =>
        int.TryParse(Environment.GetEnvironmentVariable("KCAP_EVAL_TRACE_TOKEN_BUDGET"), out var v) && v > 0
            ? v
            : DefaultTraceTokenBudget;

    /// <summary>
    /// True when the embedded compacted trace is large enough that putting it
    /// in front of the judge model risks overflowing the context window, in
    /// which case the whole session is routed through the tools path. The
    /// estimate is <paramref name="traceJsonLength"/> chars / 4 tokens.
    /// </summary>
    internal static bool ShouldForceTools(int traceJsonLength, int tokenBudget) =>
        traceJsonLength / 4 >= tokenBudget;

    /// <summary>
    /// Output of <see cref="PrepareAsync"/> — the shared state threaded
    /// through every <see cref="RunQuestionAsync"/> and finally consumed by
    /// <see cref="FinalizeAsync"/>. All fields are non-null on success.
    /// </summary>
    public sealed record EvalContext(
        string                         EvalRunId,
        string                         EncodedSessionId,
        string                         SessionId,
        string                         TraceJson,
        EvalContextResult              ContextResult,
        string                         ToolsPromptTemplate,        // still embedded (no catalog slot)
        string                         RetrospectivePrompt,        // from catalog (rendered)
        string                         RetrospectivePromptVersion, // from catalog
        IReadOnlyList<EvalQuestionDto> Questions,                  // RECONCILED from the catalog
        string                         Model,
        bool                           ForceTools,
        // The profile whose use_provider_api_key the headless judge spawns honour. Carried here
        // rather than resolved per invocation: one eval run must not straddle a profile switch.
        Profile?                       Profile,
        // Same reasoning for the home the judges read their transcripts under.
        UserHome                       Home
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
    public static async Task<SessionEvalCompletedPayloadV3?> RunAsync(
            string                          baseUrl,
            HttpClient                      httpClient,
            Profile?                        profile,
            UserHome                        home,
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
            questions ??= await EvalQuestionCatalogClient.FetchAsync(baseUrl, httpClient, observer, ct);
            if (questions is null || questions.Count == 0) {
                // FetchAsync already emitted OnFailed with a specific reason.
                // Caller-supplied empty list is rejected here without a specific reason
                // because we can't distinguish (rare edge case; both paths abort safely).
                if (questions is { Count: 0 }) observer.OnFailed("eval question catalog is empty");
                return null;
            }

            // Phase 3 — fetch the full catalog (rendered prompts + raw text +
            // versions) so PrepareAsync can reconcile the run question list from it.
            var catalog = await EvalCatalogClient.FetchAsync(baseUrl, httpClient, observer, ct);
            if (catalog is null) return null;   // FetchAsync already emitted OnFailed

            var ctx = await PrepareAsync(baseUrl, httpClient, profile, home, sessionId, questions, catalog, chain, thresholdBytes, observer, ct, model, evalRunId);
            if (ctx is null) return null;

            // Iterate the RECONCILED questions (ctx.Questions) — the text path uses
            // each question's server-rendered Prompt and Aggregate stamps the right
            // catalog PromptVersion on every verdict.
            var verdicts = new List<EvalQuestionVerdict>();
            for (var i = 0; i < ctx.Questions.Count; i++) {
                var verdict = await RunQuestionAsync(ctx, httpClient, baseUrl, ctx.Questions[i], model, i + 1, ctx.Questions.Count, observer, ct);
                if (verdict is not null) verdicts.Add(verdict);
            }

            return await FinalizeAsync(ctx, httpClient, baseUrl, verdicts, model, observer, ct);
        } catch (OperationCanceledException) {
            // Honour the contract that observers always see OnFinished or
            // OnFailed — cancellation isn't an exception path consumers
            // should have to special-case.
            observer.OnFailed("cancelled");

            return null;
        } catch (Exception ex) {
            observer.OnFailed($"eval aborted unexpectedly: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── Phase 1: Prepare ───────────────────────────────────────────────────

    public static async Task<EvalContext?> PrepareAsync(
            string                         baseUrl,
            HttpClient                     httpClient,
            Profile?                       profile,
            UserHome                       home,
            string                         sessionId,
            IReadOnlyList<EvalQuestionDto> questions,
            EvalCatalogDto                 catalog,
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
                observer.OnFailed("authentication failed — run 'kcap login' to re-authenticate");

                return null;
            }

            if (!resp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to fetch eval context: HTTP {(int)resp.StatusCode}");

                return null;
            }

            traceJson = await resp.Content.ReadAsStringAsync(ct);
            context   = JsonSerializer.Deserialize(traceJson, CapacitorJsonContext.Default.EvalContextResult);
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

        observer.OnStarted(evalRunId, model, questions.Count);

        observer.OnContextFetched(
            context.Trace.Count,
            traceJson.Length,
            context.Compaction.ToolResultsTotal,
            context.Compaction.ToolResultsTruncated,
            context.Compaction.BytesSaved
        );

        // Size-gated hybrid: if the compacted trace would overflow the judge
        // model's context window when embedded, route every question for this
        // session through the tools path (no embedded trace) instead of the
        // text-only path. See ShouldForceTools / DefaultTraceTokenBudget.
        //
        // Known limitation: the judge MCP tools for
        // summary/search/transcript/tool-result are single-session — only
        // recap/errors follow the continuation chain (and the server endpoints
        // they back have no chain support). So a chained eval (`--chain`) whose
        // trace is large enough to force tools judges the nine formerly
        // embedded-trace questions against the head session only, not the whole
        // chain. Accepted here because the alternative for an oversized chained
        // trace is the embed path's hard 400 (no verdict at all). Full
        // chain-aware tools require server-side chain support on
        // eval-summary/search/transcript — tracked separately for future work.
        var tokenBudget = TraceTokenBudget();
        var forceTools  = ShouldForceTools(traceJson.Length, tokenBudget);
        if (forceTools) {
            observer.OnInfo(
                $"trace ~{traceJson.Length / 4:N0} est. tokens exceeds budget ({tokenBudget:N0}) — "
                + "routing all questions through the tools path (no embedded trace)");
        }

        // Retained judge facts are no longer injected into per-question or
        // retrospective prompts — telling the judge "we already know about X"
        // suppresses re-reporting, which silently stops the cluster
        // occurrence_count signal from growing on recurring patterns
        // (manifests as overflowed prompts on large fact pools and as falsely
        // "stable" trends). FactsUsed snapshots persist as empty for new
        // evals; the read-side projection + UI panel stay in place so
        // historical eval audit views keep working.
        // Phase 3 — reconcile the run question list FROM the catalog (rendered
        // prompts + raw text + versions). The text path uses each question's rendered
        // Prompt directly; the tools path keeps the embedded wrapper + raw text.
        var selectedIds = questions.Select(q => q.Id).ToList();
        IReadOnlyList<EvalQuestionDto> reconciled;
        try {
            reconciled = ReconcileQuestions(selectedIds, catalog);
        } catch (ArgumentException ex) {
            observer.OnFailed($"eval catalog reconciliation failed: {ex.Message}");
            return null;
        }
        var toolsPromptTemplate = EmbeddedResources.Load("prompt-eval-question-tools.txt");

        return new EvalContext(
            EvalRunId:                  evalRunId,
            EncodedSessionId:           encodedSessionId,
            SessionId:                  context.SessionId,
            TraceJson:                  traceJson,
            ContextResult:              context,
            Profile:                    profile,
            Home:                       home,
            ToolsPromptTemplate:        toolsPromptTemplate,
            RetrospectivePrompt:        catalog.RetrospectivePrompt,
            RetrospectivePromptVersion: catalog.RetrospectivePromptVersion,
            Questions:                  reconciled,
            Model:                      model,
            ForceTools:                 forceTools
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

        // Soft-drop of {KNOWN_PATTERNS}: the prompt templates no longer carry
        // the placeholder, so the value here is inert — passed as empty string
        // for parameter-signature compatibility with the still-public
        // BuildToolsQuestionPrompt caller.
        const string patterns = "";

        // Capture ClaudeCliRunner diagnostics (exit code, stdout preview)
        // so a null result gets reported with *why* it was null — those
        // lines are the only signal about API errors or max-turn failures,
        // and daemon observers log OnInfo at Debug level where they vanish.
        var              diagnostics = new List<string>();
        ClaudeCliResult? result;

        if (question.NeedsTools || ctx.ForceTools) {
            // DEV-1486 tools-enabled path. Session-scoped MCP tool surface
            // (same as retrospective) on a per-question budget: 15 turns,
            // 10-min timeout, $1.00 cap (raised from 10/$0.50 in DEV-1576
            // after real runs hit error_max_turns mid-tool-use). Prompt
            // omits the compacted trace — the judge fetches session details
            // on demand. ctx.ForceTools additionally routes EVERY question
            // here (not just the NeedsTools four) when the session's trace is
            // too large to embed — see PrepareAsync's size gate.
            // Phase 3 — the reconciled question's Prompt is the RENDERED
            // text-path prompt; the tools template substitutes {QUESTION_TEXT}
            // from question.Prompt, so feed it the RAW catalog text instead.
            var prompt = BuildToolsQuestionPrompt(
                ctx.ToolsPromptTemplate, ctx.SessionId, ctx.EvalRunId,
                question with { Prompt = question.RawText ?? question.Prompt }, patterns);

            var commandPath = ResolveJudgeCommandPath();
            var mcpConfig   = BuildJudgeMcpConfig(commandPath, ctx.SessionId, baseUrl);

            result = await ClaudeCliRunner.RunAsync(
                prompt,
                ToolsPerQuestionTimeout,
                msg => { diagnostics.Add(msg); observer.OnInfo($"  {msg}"); },
                ctx.Profile,
                ctx.Home,
                model:          JudgeModelFor(model),
                maxTurns:       ToolsPerQuestionMaxTurns,
                promptViaStdin: true,
                jsonSchema:     VerdictJsonSchema,
                mcpConfigJson:  mcpConfig,
                allowedTools:   JudgeMcpAllowedTools,
                maxBudgetUsd:   ToolsPerQuestionMaxBudgetUsd,
                ct:             ct
            );
        } else {
            // Text-only path (default). Phase 3: the prompt is the catalog's
            // server-RENDERED prompt carried on the reconciled question — fill the
            // runtime placeholders and strip any residual {CACHE_BOUNDARY}.
            var prompt = BuildTextQuestionPrompt(question, ctx.SessionId, ctx.EvalRunId, ctx.TraceJson);

            result = await ClaudeCliRunner.RunAsync(
                prompt,
                TimeSpan.FromMinutes(5),
                msg => { diagnostics.Add(msg); observer.OnInfo($"  {msg}"); },
                ctx.Profile,
                ctx.Home,
                model:          JudgeModelFor(model),
                maxTurns:       JudgeMaxTurns,
                // Prompts embed the full compacted trace and can be hundreds
                // of KB — well past Windows' 32K argv limit. Stream via stdin.
                promptViaStdin: true,
                jsonSchema:     VerdictJsonSchema,
                ct:             ct
            );
        }

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

        // DEV-1486: record tool-call count for tools-routed questions.
        // Derived as num_turns - 1 (the final StructuredOutput turn doesn't
        // count as investigation). Clamped at 0 for the defensive case
        // where the CLI reports 0 turns. Null for text-only questions so
        // the server can distinguish "didn't measure" from "measured zero".
        // Mirrors the branch condition above so size-gate-forced questions
        // record their tool usage too.
        if (question.NeedsTools || ctx.ForceTools) {
            verdict = verdict with { ToolsUsed = Math.Max(0, result.NumTurns - 1) };
        }

        observer.OnQuestionCompleted(index, total, verdict, result.InputTokens, result.OutputTokens);

        // If the judge emitted a retain_fact, persist it for future evals.
        if (ExtractRetainFact(result.Result) is { } retainedFact) {
            if (await PostJudgeFactAsync(httpClient, baseUrl, ctx.EncodedSessionId, question.Category,
                    retainedFact.Fact, ctx.EvalRunId, retainedFact.AppliesToVendors, retainedFact.AppliesToSessionKinds,
                    observer, ct)) {
                observer.OnFactRetained(question.Category, retainedFact.Fact);
            }
        }

        return verdict;
    }

    // ── Phase 3: Finalize ──────────────────────────────────────────────────

    public static async Task<SessionEvalCompletedPayloadV3?> FinalizeAsync(
            EvalContext                        ctx,
            HttpClient                         httpClient,
            string                             baseUrl,
            IReadOnlyList<EvalQuestionVerdict> verdicts,
            string                             model,
            IEvalObserver                      observer,
            CancellationToken                  ct
        ) {
        if (verdicts.Count == 0) {
            observer.OnFailed("all judge invocations failed");

            return null;
        }

        // 4. Aggregate per-category + overall scores. Phase 3: V3 payload —
        //    each verdict is stamped with its catalog prompt version; the
        //    retrospective prompt version is stamped below from ctx.
        var aggregate = Aggregate(verdicts, ctx.EvalRunId, model, ctx.Questions);

        // FactsUsed stays empty: nothing was injected into the judge prompt,
        // so there's nothing to snapshot. The event field + projection table
        // are kept to preserve historical audit data on older evals.
        aggregate = aggregate with { FactsUsed = [] };

        // 5. Synthesise a retrospective from the per-question verdicts. Non-fatal:
        //    the verdicts are the persistence contract, a failed synthesis
        //    just leaves Retrospective=null on the payload.
        // Pass the raw session id (not EncodedSessionId) — both sinks
        // below treat this as a plain identifier: the prompt text shows
        // it to the judge verbatim, and the MCP judge subprocess receives
        // it on argv and re-encodes it at its own HTTP boundary. Passing
        // the URL-encoded form here would show the judge a mangled id
        // and make the subprocess double-encode into a URL the server
        // won't match (latent for UUID sessions, visible for
        // meta-session slugs — see DEV-1484 final review).
        var retrospective = await RunRetrospectiveAsync(
            evalRunId:           ctx.EvalRunId,
            profile:             ctx.Profile,
            home:                ctx.Home,
            sessionId:           ctx.SessionId,
            model:               model,
            baseUrl:             baseUrl,
            aggregate:           aggregate,
            verdicts:            verdicts,
            retrospectivePrompt: ctx.RetrospectivePrompt,
            traceJson:           ctx.TraceJson,
            observer:            observer,
            ct:                  ct
        );
        aggregate = aggregate with { Retrospective = retrospective };

        // Phase 3 — stamp the catalog retrospective prompt version.
        aggregate = aggregate with { RetrospectivePromptVersion = ctx.RetrospectivePromptVersion };

        // 6. Persist the aggregate to the server via the V3 route.
        var ok = await PersistAggregateV3Async(httpClient, baseUrl, ctx.EncodedSessionId, aggregate, observer, ct);
        if (!ok) return null;

        observer.OnFinished(aggregate);

        return aggregate;
    }

    /// <summary>
    /// Persists a V2 aggregate to <c>POST /api/sessions/{id}/evals/v2</c>.
    /// Extracted from <see cref="FinalizeAsync"/> as a public seam so the
    /// daemon's wire-format contract can be tested without driving a full
    /// retrospective synthesis (the latter requires the <c>claude</c> CLI
    /// which isn't available in CI).
    ///
    /// <para>
    /// Reports failures through <paramref name="observer"/>.OnFailed and
    /// returns <c>false</c>; on HTTP success returns <c>true</c> without
    /// touching the observer (caller is responsible for firing
    /// OnFinished — see <see cref="FinalizeAsync"/>).
    /// </para>
    /// </summary>
    public static async Task<bool> PersistAggregateV2Async(
            HttpClient                    httpClient,
            string                        baseUrl,
            string                        encodedSessionId,
            SessionEvalCompletedPayloadV2 aggregate,
            IEvalObserver                 observer,
            CancellationToken             ct
        ) {
        var       postUrl     = $"{baseUrl}/api/sessions/{encodedSessionId}/evals/v2";
        var       payloadJson = JsonSerializer.Serialize(aggregate, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV2);
        using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            using var postResp = await httpClient.PostWithRetryAsync(postUrl, httpContent, ct: ct);
            if (!postResp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to persist eval result: HTTP {(int)postResp.StatusCode}");

                return false;
            }
        } catch (HttpRequestException ex) {
            observer.OnFailed($"server unreachable for POST: {ex.Message}");

            return false;
        }

        return true;
    }

    /// <summary>
    /// Persists a V3 aggregate to <c>POST /api/sessions/{id}/evals/v3</c>
    /// (Phase 3 — carries per-question + retrospective prompt versions).
    /// Public seam for the daemon's wire-format contract test, mirroring
    /// <see cref="PersistAggregateV2Async"/>.
    /// </summary>
    public static async Task<bool> PersistAggregateV3Async(
            HttpClient                    httpClient,
            string                        baseUrl,
            string                        encodedSessionId,
            SessionEvalCompletedPayloadV3 aggregate,
            IEvalObserver                 observer,
            CancellationToken             ct
        ) {
        var       postUrl     = $"{baseUrl}/api/sessions/{encodedSessionId}/evals/v3";
        var       payloadJson = JsonSerializer.Serialize(aggregate, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV3);
        using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            using var postResp = await httpClient.PostWithRetryAsync(postUrl, httpContent, ct: ct);
            if (!postResp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to persist eval result: HTTP {(int)postResp.StatusCode}");
                return false;
            }
        } catch (HttpRequestException ex) {
            observer.OnFailed($"server unreachable for POST: {ex.Message}");
            return false;
        }

        return true;
    }

    // ── Prompt construction ────────────────────────────────────────────────

    /// <summary>
    /// Phase 3 — build the run question list FROM the catalog, preserving the
    /// order of <paramref name="selectedIds"/>. Each result carries the catalog's
    /// rendered <see cref="EvalQuestionDto.Prompt"/> (text path), raw
    /// <see cref="EvalQuestionDto.RawText"/> (tools path), <see cref="EvalQuestionDto.PromptVersion"/>,
    /// and <see cref="EvalQuestionDto.NeedsTools"/>. Throws if a selected id is not in
    /// the catalog (the run must judge exactly what the catalog defines).
    /// </summary>
    public static IReadOnlyList<EvalQuestionDto> ReconcileQuestions(
            IReadOnlyList<string> selectedIds,
            EvalCatalogDto        catalog
        ) {
        var byId = catalog.Questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var result = new List<EvalQuestionDto>(selectedIds.Count);
        foreach (var id in selectedIds) {
            if (!byId.TryGetValue(id, out var c))
                throw new ArgumentException($"selected question id '{id}' is not in the eval catalog");
            result.Add(new EvalQuestionDto {
                Category      = c.Category,
                Id            = c.Id,
                Text          = c.Title,
                Prompt        = c.Prompt,        // RENDERED — text path uses directly
                RawText       = c.QuestionText,  // RAW — tools path substitutes this
                NeedsTools    = c.NeedsTools,
                PromptVersion = c.PromptVersion
            });
        }
        return result;
    }

    /// <summary>Text-path prompt: the catalog rendered prompt with runtime placeholders
    /// filled and any residual {CACHE_BOUNDARY} stripped (the server runner fills it;
    /// the CLI judge has no cache boundary).</summary>
    public static string BuildTextQuestionPrompt(
            EvalQuestionDto question, string sessionId, string evalRunId, string traceJson
        ) =>
        question.Prompt
            .Replace("{CACHE_BOUNDARY}", "")
            .Replace("{SESSION_ID}",  sessionId)
            .Replace("{EVAL_RUN_ID}", evalRunId)
            .Replace("{CATEGORY}",    question.Category)
            .Replace("{QUESTION_ID}", question.Id)
            .Replace("{TRACE_JSON}",  traceJson);

    /// <summary>
    /// Builds the tools-enabled per-question prompt (DEV-1486). Mirrors the
    /// text-path <see cref="BuildTextQuestionPrompt"/> but omits <c>{TRACE_JSON}</c>
    /// — the judge pulls session details on demand via MCP instead of reading
    /// them from an embedded compacted trace.
    /// </summary>
    public static string BuildToolsQuestionPrompt(
            string          template,
            string          sessionId,
            string          evalRunId,
            EvalQuestionDto question,
            string          knownPatterns
        ) =>
        template
            .Replace("{SESSION_ID}",     sessionId)
            .Replace("{EVAL_RUN_ID}",    evalRunId)
            .Replace("{CATEGORY}",       question.Category)
            .Replace("{QUESTION_ID}",    question.Id)
            .Replace("{QUESTION_TEXT}",  question.Prompt)
            .Replace("{KNOWN_PATTERNS}", knownPatterns);

    /// <summary>
    /// Builds the retrospective synthesis prompt from the catalog-rendered
    /// template (Phase 3 — was a static embedded field) and the
    /// placeholders the judge needs: session metadata, per-question verdicts,
    /// retained patterns from prior evals in this repo, and the compacted trace.
    ///
    /// <para>
    /// SF#1 — the server retro template carries a <c>{TRACE_JSON}</c>
    /// placeholder the CLI's embedded one lacked. The daemon already fetched the
    /// trace (<see cref="EvalContext.TraceJson"/>) for the text path, so it fills
    /// the placeholder with the real trace rather than blanking it. The retro
    /// judge still has the MCP tool surface for on-demand investigation.
    /// </para>
    /// </summary>
    public static string BuildRetrospectivePrompt(
            string template,           // Phase 3 — from catalog (was static embedded field)
            string sessionMeta,
            string verdictsJson,
            string knownPatterns,
            string traceJson           // SF#1 — the daemon HAS the trace; fill it, don't blank it
        ) =>
        template
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
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("- ").Append(f.Fact);
        }

        return sb.ToString();
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
            parsed = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalQuestionVerdict);
        } catch (JsonException) {
            return null;
        }

        if (parsed is null) return null;

        if (parsed.Score is < 1 or > 5) {
            return null;
        }

        var normalisedRecommendation = parsed.Recommendation?.Trim();
        if (string.IsNullOrEmpty(normalisedRecommendation)) normalisedRecommendation = null;

        switch (parsed.Score) {
            // Contract: null recommendation at score=5, concrete recommendation
            // at score<4. Normalise score=5 unconditionally; surface score<4
            // violations but accept the verdict anyway.
            case 5 when normalisedRecommendation is not null:
                onContractViolation?.Invoke("score 5 verdict included a recommendation — nulling per contract");
                normalisedRecommendation = null;

                break;
            case < 4 when normalisedRecommendation is null:
                onContractViolation?.Invoke($"score {parsed.Score} verdict missing recommendation — accepting partial verdict"); break;
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
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalRetrospective);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// Parses a V2 retrospective synthesis response into
    /// <see cref="EvalRetrospectiveV2"/> with defensive coercions:
    /// <list type="bullet">
    ///   <item>Code fences stripped before parsing.</item>
    ///   <item>Legacy bare-string suggestion items coerced to
    ///     <c>{ Text = s, Audience = "human" }</c>.</item>
    ///   <item>Missing <c>audience</c> field → <c>"human"</c>.</item>
    ///   <item>Unknown <c>audience</c> values (not <c>"agent"</c> or
    ///     <c>"human"</c>) → <c>"human"</c>.</item>
    /// </list>
    /// Returns <c>null</c> on null/empty/whitespace input, malformed JSON,
    /// or a non-object root. Synthesis failure is non-fatal — the caller
    /// leaves <see cref="SessionEvalCompletedPayloadV2.Retrospective"/> as
    /// null.
    /// </summary>
    public static EvalRetrospectiveV2? ParseRetrospectiveV2(string rawResponse) {
        if (string.IsNullOrWhiteSpace(rawResponse)) return null;

        var json = StripCodeFences(rawResponse.Trim());

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; }

        using (doc) {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var overall   = ReadString(root, "overall") ?? "";
            var strengths = ReadStringArray(root, "strengths");
            var issues    = ReadStringArray(root, "issues");
            var suggestions = ReadSuggestionsV2(root);

            return new EvalRetrospectiveV2 {
                OverallSummary = overall,
                Strengths      = strengths,
                Issues         = issues,
                Suggestions    = suggestions
            };
        }
    }

    static List<RetrospectiveSuggestion> ReadSuggestionsV2(JsonElement root) {
        if (!root.TryGetProperty("suggestions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<RetrospectiveSuggestion>(arr.GetArrayLength());

        foreach (var item in arr.EnumerateArray()) {
            switch (item.ValueKind) {
                case JsonValueKind.String:
                    list.Add(new RetrospectiveSuggestion {
                        Text     = item.GetString() ?? "",
                        Audience = "human"
                    });
                    break;

                case JsonValueKind.Object:
                    var text         = ReadString(item, "text") ?? "";
                    var audienceRaw  = ReadString(item, "audience");
                    var normalized   = audienceRaw?.Trim().ToLowerInvariant();
                    var audience     = normalized == "agent" || normalized == "human" ? normalized : "human";
                    list.Add(new RetrospectiveSuggestion { Text = text, Audience = audience });
                    break;
            }
        }

        return list;
    }

    static string? ReadString(JsonElement parent, string propertyName) {
        if (!parent.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    static List<string> ReadStringArray(JsonElement root, string propertyName) {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray()) {
            if (item.ValueKind == JsonValueKind.String) {
                list.Add(item.GetString() ?? "");
            }
        }

        return list;
    }

    /// <summary>A retained fact plus the judge's optional applicability declaration. Both axes are
    /// null when undeclared (= applies everywhere).</summary>
    public readonly record struct RetainedFact(string Fact, string[]? AppliesToVendors, string[]? AppliesToSessionKinds);

    /// <summary>
    /// Extracts the optional <c>retain_fact</c> from a raw judge response. Accepts BOTH shapes:
    /// the plain string (back-compat) and the object
    /// <c>{"fact": "...", "applies_to_vendors": [...], "applies_to_session_kinds": [...]}</c>.
    /// Returns null when absent, explicitly null, empty-fact, or not parseable JSON. A malformed
    /// object (missing/empty fact) yields null (no fact) rather than throwing; a malformed axis
    /// (not an array of strings) is dropped to null for that axis, keeping the fact. Independent of
    /// <see cref="ParseVerdict"/> so the retained-fact plumbing doesn't depend on verdict parsing.
    /// </summary>
    public static RetainedFact? ExtractRetainFact(string rawResponse) {
        var json = StripCodeFences(rawResponse.Trim());

        try {
            using var doc = JsonDocument.Parse(json);
            // Guard before TryGetProperty: it throws InvalidOperationException on a non-object root
            // (a bare string/array/number/bool is valid JSON but not an object), which the
            // JsonException catch below would NOT swallow — the contract is malformed → null, never throw.
            if (!doc.RootElement.IsObject) {
                return null;
            }
            if (!doc.RootElement.TryGetProperty("retain_fact", out var prop)) {
                return null;
            }

            if (prop.IsString) {
                var text = prop.GetString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : new RetainedFact(text, null, null);
            }
            if (prop.IsObject) {
                var fact = prop.Str("fact")?.Trim();
                if (string.IsNullOrEmpty(fact)) return null; // object without a usable fact — skip.

                return new RetainedFact(
                    fact,
                    ReadStringArrayOrNull(prop, "applies_to_vendors"),
                    ReadStringArrayOrNull(prop, "applies_to_session_kinds"));
            }
            return null; // null / number / bool / array — no fact.
        } catch (JsonException) {
            return null;
        }
    }

    // Mirrors the schema's maxItems — a defensive cap so a hallucinating judge can't bloat the
    // outgoing payload with an unbounded applicability list.
    const int MaxApplicabilityItems = 16;

    /// <summary>Reads a JSON array of non-empty strings from <paramref name="prop"/>, or null when
    /// the field is absent, not an array, or empty. A non-string element drops the WHOLE axis to
    /// null (fail-open: the server also whole-axis-discards a malformed declaration).</summary>
    static string[]? ReadStringArrayOrNull(JsonElement prop, string name) {
        if (prop.Arr(name) is not { } arr) return null;

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray()) {
            if (!item.IsString) return null;          // malformed axis → drop it entirely.
            var v = item.GetString()?.Trim();         // trim so "codex " matches the server's exact filter
            if (!string.IsNullOrEmpty(v) && list.Count < MaxApplicabilityItems) list.Add(v);
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    static string StripCodeFences(string text) {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) {
            text = text[(firstNewline + 1)..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal)) {
            text = text[..^3].TrimEnd();
        }

        return text.Trim();
    }

    // ── Facts-used snapshot ────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="EvalFactSnapshotPayload"/> list from the
    /// per-category pool the runner fetched. Rows without a
    /// <c>FactHash</c> or <c>RetainerGitHubId</c> come from an older server
    /// that doesn't return those fields yet — they're filtered out so we only
    /// snapshot facts that can be fully attributed.
    /// </summary>
    internal static List<EvalFactSnapshotPayload> BuildFactsUsedSnapshot(
            IReadOnlyDictionary<string, List<JudgeFact>> knownFactsByCategory
        ) =>
        knownFactsByCategory
            .SelectMany(kv => kv.Value
                .Where(f => f.FactHash is not null && f.RetainerGitHubId is not null)
                .Select(f => new EvalFactSnapshotPayload {
                    Category         = kv.Key,
                    FactHash         = f.FactHash!,
                    Fact             = f.Fact,
                    RetainerGitHubId = f.RetainerGitHubId!.Value,
                    SourceSessionId  = f.SourceSessionId,
                    SourceEvalRunId  = f.SourceEvalRunId,
                    RetainedAt       = f.RetainedAt
                }))
            .ToList();

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

    public static SessionEvalCompletedPayloadV3 Aggregate(
            IReadOnlyList<EvalQuestionVerdict> verdicts,
            string                             evalRunId,
            string                             model,
            IReadOnlyList<EvalQuestionDto>     questions
        ) {
        // Phase 3 — stamp each verdict with its catalog prompt version
        // (from the reconciled questions) before grouping, so the V3 payload
        // carries per-question prompt provenance.
        var versionByQuestion = questions
            .Where(q => q.PromptVersion is not null)
            .ToDictionary(q => q.Id, q => q.PromptVersion!, StringComparer.Ordinal);

        var stamped = verdicts
            .Select(v => versionByQuestion.TryGetValue(v.QuestionId, out var ver)
                ? v with { PromptVersion = ver }
                : v)
            .ToList();

        var byCategory = stamped
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

        return new SessionEvalCompletedPayloadV3 {
            EvalRunId    = evalRunId,
            JudgeModel   = model,
            Categories   = byCategory,
            OverallScore = overall,
            Summary      = summary
            // RetrospectivePromptVersion + Retrospective filled by FinalizeAsync
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
    static async Task<EvalRetrospectiveV2?> RunRetrospectiveAsync(
            string                             evalRunId,
            Profile?                           profile,
            UserHome                           home,
            string                             sessionId,
            string                             model,
            string                             baseUrl,
            SessionEvalCompletedPayloadV3      aggregate,
            IReadOnlyList<EvalQuestionVerdict> verdicts,
            string                             retrospectivePrompt,
            string                             traceJson,
            IEvalObserver                      observer,
            CancellationToken                  ct
        ) {
        // Check cancellation before emitting OnRetrospectiveStarted so a
        // shutdown between the per-question loop and retrospective doesn't
        // leave observers seeing a started-never-finished synthesis event.
        ct.ThrowIfCancellationRequested();

        observer.OnRetrospectiveStarted();

        var sessionMeta  = $"session-id: {sessionId}\nrun-id: {evalRunId}\nmodel: {model}\noverall-score: {aggregate.OverallScore}/5";
        var verdictsJson = JsonSerializer.Serialize(verdicts, CapacitorJsonContext.Default.IReadOnlyListEvalQuestionVerdict);

        // Soft-drop of {KNOWN_PATTERNS}: template no longer has the
        // placeholder, so the retrospective prompt builder receives an
        // empty knownPatterns string and the replace becomes a no-op.
        // Phase 3: template comes from the catalog and {TRACE_JSON} is
        // filled with the daemon's already-fetched trace (SF#1).
        var prompt = BuildRetrospectivePrompt(
            retrospectivePrompt, sessionMeta, verdictsJson, knownPatterns: "", traceJson);

        // DEV-1484: instead of embedding the compacted trace (which blew
        // past Sonnet's 200K-token window on real sessions), launch a
        // per-session MCP judge server and let the judge pull recap /
        // errors / transcript slices on demand.
        var commandPath = ResolveJudgeCommandPath();
        var mcpConfig   = BuildJudgeMcpConfig(commandPath, sessionId, baseUrl);

        try {
            var result = await ClaudeCliRunner.RunAsync(
                prompt,
                RetrospectiveTimeout,
                msg => observer.OnInfo($"  {msg}"),
                profile,
                home,
                model:          JudgeModelFor(model),
                maxTurns:       RetrospectiveMaxTurns,
                // Prompt embeds verdicts + metadata; stdin keeps us below
                // Windows' 32K argv limit even without the trace.
                promptViaStdin: true,
                jsonSchema:     RetrospectiveJsonSchema,
                mcpConfigJson:  mcpConfig,
                allowedTools:   JudgeMcpAllowedTools,
                ct:             ct
            );

            if (result is null) {
                observer.OnRetrospectiveFailed("claude returned null (timeout, non-zero exit, or unparseable response)");

                return null;
            }

            // T18: V2 parser — tolerant of bare-string suggestion items
            // and missing audience field (coerces to "human"). Server-side V2
            // route accepts both shapes.
            var retrospective = ParseRetrospectiveV2(result.Result);
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

    // ── Judge-facts HTTP ───────────────────────────────────────────────────

    static async Task<bool> PostJudgeFactAsync(
            HttpClient        httpClient,
            string            baseUrl,
            string            encodedSessionId,
            string            category,
            string            fact,
            string            evalRunId,
            string[]?         appliesToVendors,
            string[]?         appliesToSessionKinds,
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        var payload = new JudgeFactPayload {
            Category              = category,
            Fact                  = fact,
            SourceEvalRunId       = evalRunId,
            AppliesToVendors      = appliesToVendors,
            AppliesToSessionKinds = appliesToSessionKinds
        };

        var       payloadJson = JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.JudgeFactPayload);
        using var content     = new StringContent(payloadJson, Encoding.UTF8, "application/json");

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

        public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) =>
            Safe(() => inner.OnStarted(evalRunId, judgeModel, totalQuestions), nameof(OnStarted));

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

        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective) =>
            Safe(() => inner.OnRetrospectiveCompleted(retrospective), nameof(OnRetrospectiveCompleted));

        public void OnRetrospectiveFailed(string reason) =>
            Safe(() => inner.OnRetrospectiveFailed(reason), nameof(OnRetrospectiveFailed));

        public void OnFinished(SessionEvalCompletedPayloadV3 aggregate) =>
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
