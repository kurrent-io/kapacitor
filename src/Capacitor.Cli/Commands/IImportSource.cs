namespace Capacitor.Cli.Commands;

/// <summary>
/// Cross-cutting filters applied to every IImportSource's discovery phase.
/// Sources that don't honour a given filter ignore the irrelevant fields.
/// </summary>
internal sealed record DiscoveryFilters(
    string?   FilterCwd,
    string?   FilterSession,
    DateOnly? Since,
    int       MinLines);

/// <summary>
/// A session candidate after DiscoverAsync. The orchestrator never inspects
/// SourceMeta; only the originating IImportSource reads it back during
/// ClassifyAsync / ImportSessionAsync.
/// </summary>
internal sealed record DiscoveredSession(
    string                                  SessionId,
    string                                  Vendor,
    string?                                 Cwd,
    DateTimeOffset?                         FirstTimestamp,
    IReadOnlyDictionary<string, object?>    SourceMeta);

/// <summary>
/// Dependencies passed to ClassifyAsync. ExcludedRepos / ExcludedPaths are
/// the user's profile-level exclusions, applied identically across sources.
/// Reimport carries the effective <c>--reimport</c> flag: when true, a source
/// that skips already-loaded sessions via a local completeness ledger must
/// bypass that ledger so the selected sessions re-classify as New/Partial and
/// re-send (idempotent server-side). Only OpenCode keeps such a ledger today;
/// every other source already re-classifies each run and ignores this field.
/// </summary>
internal sealed record ClassifyContext(
    HttpClient                  HttpClient,
    string                      BaseUrl,
    int                         MinLines,
    IReadOnlyList<string>?      ExcludedRepos,
    IReadOnlyList<string>?      ExcludedPaths,
    bool                        Reimport = false);

/// <summary>
/// Dependencies passed to ImportSessionAsync. ForcePrivate carries the
/// effective --private flag from the orchestrator; DefaultVisibility carries
/// the Step 3 setup visibility choice, or null for standalone `kcap import`.
/// Neither is read directly by a source — <see cref="VisibilityStampFor"/> is.
/// </summary>
internal sealed record ImportContext(
    HttpClient HttpClient,
    string     BaseUrl,
    bool       ForcePrivate,
    string?    DefaultVisibility = null) {
    /// <summary>
    /// The <c>default_visibility</c> to stamp on a session-start, or null to leave the field off.
    /// <b>An omitted stamp is not "no default"</b> — the server coalesces an absent one to
    /// <c>org_public</c> — so force-private must say <c>private</c> out loud.
    ///
    /// <para>Asymmetric on purpose, and neither half narrows a session that already exists: the read
    /// model's import-overlap branch omits this column, so a stamp is a creation-time value only. The
    /// Step 3 default therefore lands on New alone, while <c>private</c> is sent on every status
    /// because it costs nothing and the one status it can still reach is worth reaching. Privatising
    /// an existing session is <c>HandleImport</c>'s closing pass, not this.</para>
    /// </summary>
    public string? VisibilityStampFor(ImportCommand.ClassificationStatus status) =>
        ForcePrivate                                        ? "private"
      : status is ImportCommand.ClassificationStatus.New    ? DefaultVisibility
      :                                                       null;
}

internal enum ImportOutcome { Loaded, Resumed, Skipped, Failed }

/// <summary>
/// Result of <see cref="IImportSource.ImportSessionAsync"/>. <c>SentChildContent</c> is the
/// real "did new work actually happen" signal for nested child/subagent streams.
///
/// This field concerns ONLY nested child/subagent streams — never the call's own root stream
/// (an <c>AlreadyLoaded</c> root's own content is by definition already caught up, so
/// root-level work is always fully conveyed by <c>ImportOutcome</c> alone). It is <c>true</c>
/// iff, during this call, transcript lines were POSTed to a nested child stream. Where a
/// reliable per-child watermark is known, <c>true</c> means lines beyond that watermark were
/// sent. Where the watermark probe fails or no reliable watermark exists, the conservative
/// default is to attempt a full resend and report <c>true</c> whenever that resend actually
/// posts lines — this signal deliberately ERRS TOWARD permitting a possibly-duplicate resend
/// rather than silently suppressing genuinely-new content; it is NOT a claim that the server
/// was verified to lack that content beforehand.
///
/// <para>
/// <b>This field is meaningful ONLY when the call's own result is non-<c>Failed</c>.</b> On a
/// <c>Failed</c> result the field's value is UNSPECIFIED and MUST NOT be consulted for any
/// purpose — see <see cref="ImportCommand.ResolveRoutedOutcomeForCounting"/>, whose resolver
/// never consults <c>sentChildContent</c> on a <c>Failed</c> outcome.
/// </para>
/// </summary>
internal readonly record struct ImportSessionResult(ImportOutcome Outcome, bool SentChildContent = false) {
    public static implicit operator ImportSessionResult(ImportOutcome outcome) => new(outcome);
}

/// <summary>
/// Per-vendor import pipeline: discover candidate sessions on the local
/// machine, classify them against the server's existing state, then import
/// the ones that need work. Three implementations: ClaudeImportSource,
/// CodexImportSource, CursorImportSource.
/// </summary>
internal interface IImportSource {
    /// <summary>"claude" | "codex" | "cursor". Stamped onto every produced classification.</summary>
    string Vendor { get; }

    /// <summary>True when the source's root data dir / DB is present on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// How old a discovered session is, by the rule THIS source's <c>--since</c> applies to it — so a
    /// window's count predicts the import that window would produce.
    /// </summary>
    /// <remarks>
    /// Most sources resolve a <c>FirstTimestamp</c> during discovery and filter on it, which is the
    /// default. Claude and Codex do not and override: their <c>--since</c> reads a rollout's day
    /// directory and a transcript's first timestamp respectively. Answering here rather than in a
    /// switch keeps a new harness from having to edit shared code to be dated correctly.
    /// </remarks>
    DateTimeOffset? DiscoveryAge(DiscoveredSession session) => session.FirstTimestamp;

    /// <summary>
    /// True if title-generation background tasks should be scheduled for
    /// sessions imported via this source. Claude and Codex return true;
    /// Cursor returns false because the composer header carries a name that
    /// the server maps to a SessionTitleCreatedEvent during ingest.
    /// </summary>
    bool SupportsTitleGeneration { get; }

    /// <summary>
    /// True if a routed <see cref="ImportSessionAsync"/> call for a
    /// <see cref="ImportCommand.ClassificationStatus.AlreadyLoaded"/> classification can still POST
    /// transcript lines — to a nested child/subagent stream, the root's own content being caught up
    /// by definition. Such a call can add publicly-visible content to a session that already
    /// exists, so <c>--private</c> privatizes these sources independently of the call's
    /// <see cref="ImportOutcome"/> (see <c>privateScopeSessionIds</c> in
    /// <see cref="ImportCommand"/>). The outcome can't be trusted to reveal the attach: a source
    /// may report a hardcoded <see cref="ImportOutcome.Skipped"/>, and any lifecycle POST can fail
    /// AFTER the child content persisted. The replayed session-start's <c>default_visibility</c>
    /// is a create-time hint, so it does nothing here either.
    ///
    /// <para>
    /// <c>false</c> says only that the <c>AlreadyLoaded</c> call posts no transcript content — not
    /// that it posts nothing at all (Copilot/Kiro/Pi still replay session-start/session-end
    /// lifecycle there; they simply have no child import) and not that the source never adds
    /// content (New/Partial paths are out of scope for this flag). Per-source values are pinned by
    /// <c>ReplayChildContentCapabilityTests</c>.
    /// </para>
    /// </summary>
    bool AttachesChildContentOnReplay { get; }

    Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(
        DiscoveryFilters  filters,
        CancellationToken ct);

    Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
        IReadOnlyList<DiscoveredSession> sessions,
        ClassifyContext                   ctx,
        CancellationToken                 ct);

    Task<ImportSessionResult> ImportSessionAsync(
        ImportCommand.SessionClassification classification,
        ImportContext                       ctx,
        CancellationToken                   ct);
}
