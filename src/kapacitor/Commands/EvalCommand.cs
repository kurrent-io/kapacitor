using kapacitor.Eval;

namespace kapacitor.Commands;

/// <summary>
/// Thin CLI adapter over <see cref="EvalService"/>: parses arg flags,
/// provides a stderr-logging observer, renders the final aggregate as a
/// terminal report. The eval pipeline itself lives in the Eval library so
/// the daemon (DEV-1440 milestone 2) can reuse it.
/// </summary>
static class EvalCommand {
    public static async Task<int> HandleEval(string baseUrl, string sessionId, string model, bool chain, int? thresholdBytes) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        var observer = new ConsoleEvalObserver(sessionId);
        var result   = await EvalService.RunAsync(baseUrl, httpClient, sessionId, model, chain, thresholdBytes, observer);

        if (result is null) {
            return 1;
        }

        Render(result, sessionId);
        return 0;
    }

    static void Render(SessionEvalCompletedPayload agg, string sessionId) {
        var output = Console.Out;
        output.WriteLine();
        output.WriteLine($"Eval results for session {sessionId}");
        output.WriteLine($"Model: {agg.JudgeModel}   Run: {agg.EvalRunId}");
        output.WriteLine(new string('─', 72));

        foreach (var cat in agg.Categories) {
            output.WriteLine();
            output.WriteLine($"  {cat.Name,-16}  {cat.Score}/5  [{cat.Verdict}]");

            foreach (var q in cat.Questions) {
                var marker = q.Verdict switch {
                    "pass" => "✓",
                    "warn" => "!",
                    _      => "✗"
                };
                output.WriteLine($"    {marker} {q.QuestionId,-26} {q.Score}  {q.Finding}");
                if (!string.IsNullOrEmpty(q.Evidence)) {
                    output.WriteLine($"        evidence: {q.Evidence}");
                }
            }
        }

        output.WriteLine();
        output.WriteLine(new string('─', 72));
        output.WriteLine($"  Overall: {agg.OverallScore}/5  [{EvalService.VerdictForScore(agg.OverallScore)}]");
        output.WriteLine($"  {agg.Summary}");
        output.WriteLine();
    }

    /// <summary>
    /// Renders every eval progress callback to stderr with a consistent
    /// <c>[HH:mm:ss] [eval] …</c> prefix, matching the pre-refactor shape
    /// of <c>kapacitor eval</c>'s output.
    /// </summary>
    sealed class ConsoleEvalObserver(string sessionId) : IEvalObserver {
        public void OnInfo(string message) => Log(message);

        public void OnStarted(string evalRunId, string contextSessionId, string judgeModel, int totalQuestions) =>
            Log($"Evaluating session {sessionId} (run {evalRunId}, model {judgeModel}, {totalQuestions} questions)");

        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
            Log($"Fetched {traceEntries} trace entries ({toolResultsTotal} tool results, {toolResultsTruncated} truncated, {bytesSaved} bytes saved). Trace size: {traceChars} chars.");

        public void OnQuestionStarted(int index, int total, string category, string questionId) =>
            Log($"[{index}/{total}] {category}/{questionId}...");

        public void OnQuestionCompleted(int index, int total, EvalQuestionVerdict verdict, long inputTokens, long outputTokens) =>
            Log($"  {verdict.QuestionId} done (input={inputTokens}, output={outputTokens})");

        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) =>
            Log($"  {questionId} failed: {reason}");

        public void OnFactRetained(string category, string fact) =>
            Log($"  retained fact for category {category}");

        public void OnFinished(SessionEvalCompletedPayload aggregate) =>
            Log("Eval result persisted.");

        public void OnFailed(string reason) =>
            Console.Error.WriteLine(reason);

        static void Log(string message) =>
            Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] [eval] {message}");
    }
}
