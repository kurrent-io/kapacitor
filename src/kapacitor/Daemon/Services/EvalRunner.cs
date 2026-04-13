using kapacitor.Eval;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

/// <summary>
/// DEV-1440 milestone 2 — handles <see cref="RunEvalCommand"/> dispatched
/// from the dashboard via SignalR. The daemon pulls an authenticated
/// HTTP client, runs <see cref="EvalService"/> with a
/// <see cref="DaemonEvalObserver"/> that relays progress back over the
/// SignalR connection, and completes asynchronously — the SignalR
/// <c>RunEval</c> hub invocation fires-and-forgets; progress comes back
/// through <c>EvalStarted</c> / <c>EvalQuestionCompleted</c> / etc.
/// </summary>
public sealed class EvalRunner {
    readonly ServerConnection    _connection;
    readonly ILogger<EvalRunner> _logger;
    readonly string              _baseUrl;
    readonly CancellationToken   _shutdownToken;

    public EvalRunner(
            ServerConnection         connection,
            DaemonConfig             config,
            IHostApplicationLifetime lifetime,
            ILogger<EvalRunner>      logger
        ) {
        _connection    = connection;
        _logger        = logger;
        _baseUrl       = config.ServerUrl.TrimEnd('/');
        _shutdownToken = lifetime.ApplicationStopping;

        _connection.OnRunEval += HandleRunEvalAsync;
    }

    async Task HandleRunEvalAsync(RunEvalCommand cmd) {
        _logger.LogInformation(
            "Dispatched eval {RunId} for session {Sid} (model {Model}, chain={Chain})",
            cmd.EvalRunId, cmd.SessionId, cmd.Model, cmd.Chain
        );

        // Fire-and-forget so the SignalR hub invocation doesn't block; the
        // eval's own observer callbacks fan progress back to the server.
        // Daemon shutdown cancels the token so in-flight evals get a clean
        // OnFailed("cancelled") rather than dying mid-judge. Any unhandled
        // exception from the run is caught and translated to an EvalFailed
        // event so the dashboard learns about it either way.
        _ = Task.Run(async () => {
            try {
                using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
                var observer = new DaemonEvalObserver(_connection, cmd.EvalRunId, cmd.SessionId, _logger);

                await EvalService.RunAsync(
                    _baseUrl,
                    httpClient,
                    cmd.SessionId,
                    cmd.Model,
                    cmd.Chain,
                    cmd.ThresholdBytes,
                    observer,
                    ct: _shutdownToken,
                    // Use the dispatched run id so all progress events and
                    // the persisted aggregate share the same correlation id
                    // the dashboard already knows about.
                    evalRunId: cmd.EvalRunId
                );
            } catch (Exception ex) {
                _logger.LogError(ex, "Unhandled exception running eval {RunId} on session {Sid}", cmd.EvalRunId, cmd.SessionId);

                try {
                    await _connection.EvalFailedAsync(cmd.EvalRunId, cmd.SessionId, $"daemon error: {ex.GetType().Name}");
                } catch (Exception relayEx) {
                    _logger.LogError(relayEx, "Failed to relay EvalFailed for eval {RunId}", cmd.EvalRunId);
                }
            }
        });
    }
}

/// <summary>
/// <see cref="IEvalObserver"/> implementation that pushes the shaped
/// callbacks — <see cref="OnStarted"/>, <see cref="OnQuestionCompleted"/>,
/// <see cref="OnFinished"/>, <see cref="OnFailed"/> — over the daemon's
/// SignalR connection so the dashboard can render live progress. Info /
/// per-question-start / per-question-failure / fact-retained callbacks
/// just log locally; they're not interesting enough to justify SignalR
/// chatter for every judge.
/// </summary>
sealed class DaemonEvalObserver(
        ServerConnection connection,
        string           evalRunId,
        string           sessionId,
        ILogger          logger
    ) : IEvalObserver {
    // Serialize SignalR relays so concurrent Task.Runs don't interleave;
    // the dashboard expects EvalStarted → question completions →
    // EvalFinished/EvalFailed in order. SemaphoreSlim suffices because
    // the observer is called synchronously from EvalService — only the
    // async send to SignalR could otherwise reorder.
    readonly SemaphoreSlim _relayLock = new(1, 1);

    public void OnInfo(string message) =>
        logger.LogDebug("[eval {Run}] {Message}", evalRunId, message);

    public void OnStarted(string runId, string contextSessionId, string judgeModel, int totalQuestions) {
        logger.LogInformation("Eval {Run} started on session {Sid} (model {Model}, {Count} questions)", runId, sessionId, judgeModel, totalQuestions);
        Relay(() => connection.EvalStartedAsync(runId, sessionId, judgeModel, totalQuestions), "EvalStarted");
    }

    public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
        logger.LogDebug("Eval {Run} context fetched: {Entries} entries, {Chars} chars", evalRunId, traceEntries, traceChars);

    public void OnQuestionStarted(int index, int total, string category, string questionId) =>
        logger.LogDebug("[eval {Run}] [{Index}/{Total}] {Category}/{Question} started", evalRunId, index, total, category, questionId);

    public void OnQuestionCompleted(int index, int total, EvalQuestionVerdict verdict, long inputTokens, long outputTokens) {
        logger.LogInformation(
            "[eval {Run}] [{Index}/{Total}] {Question} -> {Score} ({Verdict})",
            evalRunId, index, total, verdict.QuestionId, verdict.Score, verdict.Verdict
        );
        Relay(() => connection.EvalQuestionCompletedAsync(evalRunId, sessionId, index, total, verdict.Category, verdict.QuestionId, verdict.Score, verdict.Verdict), "EvalQuestionCompleted");
    }

    public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) =>
        logger.LogWarning("[eval {Run}] [{Index}/{Total}] {Category}/{Question} failed: {Reason}", evalRunId, index, total, category, questionId, reason);

    public void OnFactRetained(string category, string fact) =>
        logger.LogDebug("[eval {Run}] retained fact for {Category}: {Fact}", evalRunId, category, fact);

    public void OnFinished(SessionEvalCompletedPayload aggregate) {
        logger.LogInformation("Eval {Run} finished on session {Sid}: {Score}/5", evalRunId, sessionId, aggregate.OverallScore);
        Relay(() => connection.EvalFinishedAsync(evalRunId, sessionId, aggregate.OverallScore, aggregate.Summary), "EvalFinished");
    }

    public void OnFailed(string reason) {
        logger.LogWarning("Eval {Run} failed on session {Sid}: {Reason}", evalRunId, sessionId, reason);
        Relay(() => connection.EvalFailedAsync(evalRunId, sessionId, reason), "EvalFailed");
    }

    void Relay(Func<Task> send, string eventName) {
        _ = Task.Run(async () => {
            await _relayLock.WaitAsync();
            try {
                await send();
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to relay {Event} for eval {Run}", eventName, evalRunId);
            } finally {
                _relayLock.Release();
            }
        });
    }
}
