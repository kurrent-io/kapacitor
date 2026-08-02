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
        "clear"            => SessionLifecycleReason.Clear,
        null or ""         => SessionLifecycleReason.New,
        _                  => SessionLifecycleReason.Unknown
    };

    /// <summary>
    /// The lease-key discriminator for one context reset, or null for every other reason.
    ///
    /// <para>Null is what preserves the existing behaviour EXACTLY: it hashes to the byte-identical legacy
    /// lease key, so a session started under a pre-generation CLI keeps its completed lease and a newer
    /// hook firing into it stays silent. No dual-read migration is needed because generation zero was
    /// always spelled this way.</para>
    ///
    /// <para><b>The id must distinguish two genuine clears while collapsing a redelivery of one.</b> That
    /// is a property of what the host actually sends, and it differs by harness — measured, not assumed:
    /// Gemini's SessionStart payload carries a <c>timestamp</c>, which changes between clears and is
    /// identical on a redelivery, giving exactly-once. Claude's carries only session id, transcript path,
    /// cwd and source, none of which differ between two clears of one session — so no usable id exists and
    /// the caller must pass a per-invocation value, accepting AT-LEAST-ONCE. A redelivered Claude clear can
    /// therefore inject twice; that is the documented trade, and it is strictly better than the current
    /// behaviour of never re-injecting at all.</para>
    /// </summary>
    public static string? ContextResetInstanceId(SessionLifecycleReason reason, string? resetDiscriminator) =>
        reason == SessionLifecycleReason.Clear && resetDiscriminator is { Length: > 0 }
            ? "clear:" + resetDiscriminator
            : null;
}
