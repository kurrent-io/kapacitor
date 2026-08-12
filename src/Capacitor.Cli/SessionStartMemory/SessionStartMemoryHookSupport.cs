using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The two hook-side concerns every SessionStart memory adapter needs and must not get wrong,
/// factored out of the per-vendor hook commands so the next adapter inherits them instead of
/// rediscovering them. Both were real defects found in review of the Codex adapter.
///
/// <para>The vendor hooks own their envelope, eligibility and ordering — those genuinely differ per
/// harness. Only these two are identical everywhere, so only these two live here.</para>
/// </summary>
internal static class SessionStartMemoryHookSupport {
    /// <summary>
    /// Whether memory injection may attempt auth discovery for <paramref name="baseUrl"/> at all.
    ///
    /// <para>MUST be checked before any client construction. The authenticated-client helper funnels
    /// through <c>EnsureAbsolute</c>, which prints a hint and calls <c>Environment.Exit(2)</c> on a
    /// URL it cannot accept. From a hook whose host blocks on (or parses) stdout, exiting there kills
    /// the process before the required output is written, so the harness sees nothing and rejects the
    /// session — strictly worse than silently skipping an optional memory fragment.</para>
    ///
    /// <para>Deliberately the SAME predicate <c>EnsureAbsolute</c> itself uses, so this guard can
    /// never disagree with the validator it exists to protect. Single-sourced through
    /// <see cref="HookHttp.IsPostable"/>.</para>
    /// </summary>
    public static bool CanAttempt(string? baseUrl) => HookHttp.IsPostable(baseUrl);

    /// <summary>
    /// The production memory-index client factory: authenticated, and honouring the provider's
    /// 401-refresh contract.
    ///
    /// <para><c>/api/memories/index</c> is bearer-authenticated, and
    /// <see cref="SessionStartMemoryContextProvider"/> hands the REJECTED bearer back to this factory
    /// after a 401 so it can mint a refreshed client. A bare <c>new HttpClient()</c> would go out
    /// anonymous on both the initial call and the refresh: the provider records a retryable failure
    /// and the harness silently never receives memory context on any authenticated deployment.</para>
    /// </summary>
    public static Func<string?, CancellationToken, Task<HttpClient>> ClientFactory(string baseUrl)
        => async (rejectedAccessToken, ct) => (await HttpClientExtensions.CreateClientWithAuthStatusAsync(
            baseUrl, ct, allowAutoRedirect: false, rejectedAccessToken: rejectedAccessToken)).Client;

    /// <summary>
    /// Builds the combined memory + guidelines SessionStart context provider. The memory
    /// lane and the guidelines lane share one authenticated client factory and the composite resolves
    /// the repo/machine scope ONCE for both. Which lanes actually run is decided per request via
    /// <see cref="SessionStartMemoryContextRequest.Disabled"/> (memory) and its
    /// <c>GuidelinesDisabled</c> flag — a disabled lane contributes nothing.
    ///
    /// <para>This is the single construction site for the eight non-Claude harnesses. Claude does NOT
    /// use it — it keeps a memory-only <see cref="SessionStartMemoryContextProvider"/> and renders
    /// guidelines from its own hook POST response.</para>
    ///
    /// <para>The caller resolves its own <paramref name="clientFactory"/> (each adapter keeps its
    /// factory choice) and passes <paramref name="disposeClients"/>: true when the factory is one we
    /// created (ours to dispose), false for a test/injected factory whose client belongs to its caller
    /// and may be handed back on the 401-refresh call. Both lanes share the one factory.</para>
    /// </summary>
    public static ISessionStartContextProvider CompositeProvider(
            Func<string?, CancellationToken, Task<HttpClient>> clientFactory,
            bool disposeClients,
            ISessionStartMemoryScopeResolver? scopeResolver = null) {
        var resolver = scopeResolver ?? new SessionStartMemoryScopeResolver();

        var memory     = new SessionStartMemoryContextProvider(resolver, clientFactory, disposeClients: disposeClients);
        var guidelines = new SessionStartGuidelinesLane(clientFactory, disposeClients: disposeClients);
        return new SessionStartCompositeContextProvider(resolver, memory, guidelines);
    }

    /// <summary>
    /// Awaits an in-flight fragment fetch under the budget remaining AT THIS INSTANT — never the
    /// budget it was started with. On expiry the fetch is abandoned rather than cancelled mid-flight
    /// (its own lease bookkeeping owns that) and null is returned, so the caller's output degrades to
    /// "no memory" instead of being delayed past the harness's hook ceiling. Never throws.
    ///
    /// <para><see cref="HookBudget.Remaining"/> ALREADY reserves <see cref="HookBudget.Safety"/> for
    /// serialization and the write itself — do not subtract it again here or at the call site. Doing
    /// so cut the usable window from 3.5s to 2s at a fresh hook start and silently discarded healthy
    /// 2–3.5s responses that fit the intended ceiling.</para>
    /// </summary>
    public static async Task<string?> AwaitBounded(Task<string?> task, long processStart, string command) {
        try {
            var budget = HookBudget.Remaining(processStart, command);

            if (budget <= TimeSpan.Zero)
                return task.IsCompletedSuccessfully ? task.Result : null;

            return await task.WaitAsync(budget);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Maps a harness-reported SessionStart source to the shared lifecycle reason. Unknown values map
    /// to <see cref="SessionLifecycleReason.Unknown"/> rather than being guessed as New — the lifecycle
    /// policy decides eligibility from this, so inventing a reason would invent an injection decision.
    /// </summary>
    public static SessionLifecycleReason ReasonFor(string? source) => source?.ToLowerInvariant() switch {
        "startup" or "new" => SessionLifecycleReason.New,
        "resume"           => SessionLifecycleReason.Resume,
        "reopen"           => SessionLifecycleReason.Reopen,
        "fork"             => SessionLifecycleReason.Fork,
        "compact"          => SessionLifecycleReason.Compact,
        null or ""         => SessionLifecycleReason.New,
        _                  => SessionLifecycleReason.Unknown
    };
}
