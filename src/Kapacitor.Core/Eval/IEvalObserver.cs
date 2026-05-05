namespace kapacitor.Eval;

/// <summary>
/// Progress surface for an eval run. The CLI implementation writes each
/// callback to stderr; the daemon implementation (DEV-1440 milestone 2)
/// pushes every per-run and per-question transition
/// (<see cref="OnStarted"/>, <see cref="OnQuestionStarted"/>,
/// <see cref="OnQuestionCompleted"/>, <see cref="OnQuestionFailed"/>,
/// <see cref="OnFinished"/>, <see cref="OnFailed"/>) over SignalR so the
/// dashboard can render live progress — including failed judges —
/// while they run on the user's machine.
/// <see cref="OnInfo"/>, <see cref="OnContextFetched"/>, and
/// <see cref="OnFactRetained"/> are daemon-local (debug logs only) since
/// the dashboard has no rendering for them.
///
/// <para>
/// Callbacks are fired from the running eval task but must not perform
/// long-running work synchronously — observers that need to do I/O should
/// fan out to a background queue. Exceptions from an observer are caught
/// by an internal SafeObserver wrapper inside EvalService and logged to
/// stderr; they don't abort the eval. Cancellation via the
/// <c>CancellationToken</c> passed to <c>RunAsync</c> also surfaces as
/// <see cref="OnFailed"/>("cancelled") so observers always see exactly
/// one terminal callback.
/// </para>
/// </summary>
internal interface IEvalObserver {
    /// <summary>Free-form informational message — CLI logs, daemon generally ignores.</summary>
    void OnInfo(string message);

    /// <summary>Fired once after the eval context has been fetched and the judges are about to run.</summary>
    void OnStarted(string evalRunId, string judgeModel, int totalQuestions);

    /// <summary>Informational — context fetched, compaction stats available.</summary>
    void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved);

    /// <summary>Fired just before each judge question is sent to Claude.</summary>
    void OnQuestionStarted(int index, int total, string category, string questionId);

    /// <summary>Fired after a judge question completed successfully and its verdict was parsed.</summary>
    void OnQuestionCompleted(int index, int total, EvalQuestionVerdict verdict, long inputTokens, long outputTokens);

    /// <summary>Fired when a judge question fails (null Claude result, unparseable JSON, etc.); the eval continues.</summary>
    void OnQuestionFailed(int index, int total, string category, string questionId, string reason);

    /// <summary>Fired when the judge produced a retain_fact and the CLI successfully POSTed it to the server.</summary>
    void OnFactRetained(string category, string fact);

    /// <summary>Fired just before the retrospective synthesis prompt is sent to the judge model.</summary>
    void OnRetrospectiveStarted();

    /// <summary>Fired after the retrospective completed successfully and its payload was parsed.</summary>
    void OnRetrospectiveCompleted(EvalRetrospective retrospective);

    /// <summary>Fired when retrospective synthesis failed (null Claude result, unparseable JSON, etc.); the eval still completes.</summary>
    void OnRetrospectiveFailed(string reason);

    /// <summary>Fired once after all judges finished, results aggregated, and the aggregate POSTed to the server.</summary>
    void OnFinished(SessionEvalCompletedPayload aggregate);

    /// <summary>Fired when the eval failed before producing an aggregate (e.g. context fetch failed, all judges failed, persist failed).</summary>
    void OnFailed(string reason);
}
