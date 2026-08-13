using System.Reflection;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

public static class UpdateCommand {
    /// <summary>
    /// npm registry base URL. Overridable seam so integration tests can point
    /// the CLI at a fake registry (e.g. WireMock) instead of the real npm registry.
    /// </summary>
    internal static string RegistryBaseUrl = "https://registry.npmjs.org";

    /// <summary>Valid npm dist-tags for the update channel (Phase 1).</summary>
    static readonly string[] KnownChannels = ["latest", "beta"];

    /// <summary>
    /// Version-transition arrow. ASCII on Windows: legacy console codepages
    /// (cp437/cp850) can't encode `→` and render it as `␦`/`?`.
    /// Internal (not private): <see cref="Capacitor.Cli.UpdateNotice"/> renders the same hint
    /// text from its own exit-time print path and must not drift from this formatting.
    /// </summary>
    internal static readonly string Arrow = OperatingSystem.IsWindows() ? "->" : "→";

    /// <summary>
    /// Resolves the effective update channel (npm dist-tag): an explicit
    /// <c>--beta</c>/<c>--stable</c> flag wins, otherwise the configured channel
    /// is used. The result is validated against <see cref="KnownChannels"/> and
    /// falls back to <c>"latest"</c> for anything unset/blank/unknown. This is the
    /// single trust boundary for the channel value: it flows into a filesystem
    /// cache path (<c>update-check-{channel}.json</c>) and the registry URL, so a
    /// corrupted or hand-edited <c>update_channel</c> (a typo, or one containing
    /// <c>..</c> or a rooted path) must never reach either unsanitized. STJ
    /// source-gen also doesn't apply the record default on deserialize, so a real
    /// profile can carry a null <c>UpdateChannel</c> — also handled here.
    /// </summary>
    internal static string ResolveChannel(string[] args, string? configuredChannel) {
        if (args.Contains("--stable")) return "latest";
        if (args.Contains("--beta"))   return "beta";
        var channel = configuredChannel?.Trim().ToLowerInvariant();
        return channel is not null && KnownChannels.Contains(channel) ? channel : "latest";
    }

    public static async Task<int> HandleAsync(string[] args) {
        var profile   = await AppConfig.GetActiveProfileAsync();
        var channel   = ResolveChannel(args, profile?.UpdateChannel);
        var checkOnly = args.Contains("--check");

        // Persist an explicit channel switch onto the active profile so future
        // auto-updates track it. Update the profile inside ProfileConfig and save
        // the whole v2 config via ConfigMutator — NEVER write a flat
        // LegacyV1Config, which would overwrite the user's v2 profile config.
        if (args.Contains("--beta") || args.Contains("--stable")) {
            var pc = await AppConfig.LoadProfileConfig();

            // Must match the profile GetActiveProfileAsync() resolved above (the
            // read side): when a profile was resolved (env var, .kcap.json, git
            // remote match, or `kcap use` binding), persist to THAT profile —
            // not blindly to the on-disk active_profile — so the switch sticks
            // for the profile the user is actually on.
            var targetName = AppConfig.ResolvedProfile?.ProfileName ?? pc.ActiveProfile;
            if (pc.Profiles.TryGetValue(targetName, out var active)
             && active.UpdateChannel != channel) {
                await ConfigMutator.MutateAsync(c => {
                    if (!c.Profiles.TryGetValue(targetName, out var current))
                        return c;

                    return c with {
                        Profiles = new Dictionary<string, Profile>(c.Profiles) {
                            [targetName] = current with { UpdateChannel = channel }
                        }
                    };
                });
            }
        }

        var checkResult       = await CheckForUpdateAsync(forceCheck: true, channel);
        var (latest, current) = (checkResult.Latest, checkResult.Current);

        if (checkOnly) {
            // Machine-readable probe consumed by the npm launcher (kcap.js).
            // One JSON line on stdout; exit 1 only when the check itself failed.
            //
            // `newer` is a tri-state: true => upgrade, false => confidently up to
            // date, null => can't tell (current version unknown or registry check
            // failed). The launcher must NOT skip on null — otherwise a binary
            // that reports "unknown" would strand the user on a stale CLI.
            bool? newer = string.IsNullOrEmpty(current) || latest is null
                ? null
                : IsNewer(latest, current);

            var obj = new JsonObject {
                ["current"]     = current,
                ["latest"]      = latest,
                ["newer"]       = newer,
                ["channel"]     = channel,
                ["install_tag"] = channel,
            };

            await Console.Out.WriteLineAsync(obj.ToJsonString());

            return latest is null ? 1 : 0;
        }

        if (latest is null) {
            await Console.Error.WriteLineAsync("Could not check for updates.");

            return 1;
        }

        if (string.IsNullOrEmpty(current)) {
            await Console.Error.WriteLineAsync($"Could not determine the current kcap version. Latest published: {latest}.");

            return 1;
        }

        if (!IsNewer(latest, current)) {
            await Console.Out.WriteLineAsync($"Already up to date: {current}");

            return 0;
        }

        // Reached only when the native binary is run WITHOUT the npm launcher
        // (e.g. invoking the platform binary directly). For npm-global installs
        // the launcher intercepts `update` and performs the upgrade itself.
        await Console.Out.WriteLineAsync($"Update available: {current} {Arrow} {latest}");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("Run `kcap update` to update, or upgrade directly:");
        await Console.Out.WriteLineAsync("  npm install -g @kurrent/kcap@latest");

        return 0;
    }

    /// <summary>
    /// Print an update hint to stderr if a newer version is available. Budget-aware
    /// (<see cref="CheckForUpdateWithBudgetAsync"/>) so a slow/unreachable registry can't stall
    /// the caller. Retained as a standalone, no-args entry point; the actual exit-time call site
    /// is <see cref="Capacitor.Cli.UpdateNotice.FlushAsync"/>, which additionally applies the
    /// human-facing suppression predicate and a cross-surface "already reported" gate so this
    /// text is never printed twice for one invocation.
    /// </summary>
    public static async Task PrintUpdateHintIfAvailable() {
        try {
            var profile = await AppConfig.GetActiveProfileAsync();
            if (profile?.UpdateCheck == false) return;
            var channel     = ResolveChannel([], profile?.UpdateChannel);
            var checkResult = await CheckForUpdateWithBudgetAsync(channel);

            if (checkResult is { Newer: true, Latest: not null, Current: not null }) {
                await Console.Error.WriteLineAsync();
                await Console.Error.WriteLineAsync($"Update available: {checkResult.Current} {Arrow} {checkResult.Latest}");
                await Console.Error.WriteLineAsync("Run `kcap update` to update");
            }
        } catch {
            // Best effort — never break the CLI for update checks
        }
    }

    /// <summary>
    /// Two-tier budget over <see cref="CheckForUpdateAsync"/> for a passive, exit-time caller
    /// (<see cref="Capacitor.Cli.UpdateNotice"/>): the common cache-fresh case (a local file
    /// read, no network — see <see cref="UpdateCacheRecord.IsFresh"/>) is bound by a defensive
    /// <paramref name="cacheFreshBudget"/> (default ~300ms); a stale/missing cache instead rides
    /// the fetch's own cancellation deadline (<paramref name="networkCancelAfter"/>, default
    /// ~3s, passed as the fetch's <c>ct</c>) plus a short <paramref name="cleanupGrace"/>
    /// (default ~500ms) so the failure/backoff write — deliberately unbound by that token, see
    /// <see cref="WriteCacheRecordAsync"/> — can still land on disk.
    /// </summary>
    /// <returns>
    /// The completed result, or <c>null</c> if neither tier produced one in time. A still-running
    /// check past that point is abandoned (never awaited further by the caller) but not orphaned:
    /// a continuation disposes its <see cref="CancellationTokenSource"/> once it eventually
    /// finishes and observes any fault so it can't surface as an unobserved task exception.
    /// </returns>
    internal static async Task<UpdateCheckResult?> CheckForUpdateWithBudgetAsync(
            string channel,
            TimeSpan? cacheFreshBudget = null,
            TimeSpan? networkCancelAfter = null,
            TimeSpan? cleanupGrace = null) {
        var cacheFreshBudgetVal   = cacheFreshBudget ?? TimeSpan.FromMilliseconds(300);
        var networkCancelAfterVal = networkCancelAfter ?? TimeSpan.FromSeconds(3);
        var cleanupGraceVal       = cleanupGrace ?? TimeSpan.FromMilliseconds(500);

        var cts       = new CancellationTokenSource(networkCancelAfterVal);
        var checkTask = CheckForUpdateAsync(forceCheck: false, channel, cts.Token);

        // Dispose only once the task reaches a terminal state — never synchronously here, since
        // an abandoned check (either tier below giving up) may still be running past this method's
        // own return. Also observes a fault so a late exception can't become unobserved.
        _ = checkTask.ContinueWith(static (t, state) => {
            ((CancellationTokenSource)state!).Dispose();
            if (t.IsFaulted) _ = t.Exception;
        }, cts, TaskScheduler.Default);

        var firstWinner = await Task.WhenAny(checkTask, Task.Delay(cacheFreshBudgetVal));
        if (firstWinner == checkTask) {
            return checkTask.IsCompletedSuccessfully ? checkTask.Result : null;
        }

        // Cache was stale/missing (the fetch above didn't return from a local file read within
        // the defensive bound) — a network refresh is in flight. Let the cancellation already
        // armed on `cts` cut the HTTP request at networkCancelAfterVal, then allow cleanupGraceVal
        // more for the CancellationToken.None-guarded backoff write to persist.
        var remaining    = networkCancelAfterVal - cacheFreshBudgetVal + cleanupGraceVal;
        var secondWinner = await Task.WhenAny(checkTask, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero));

        return secondWinner == checkTask && checkTask.IsCompletedSuccessfully ? checkTask.Result : null;
    }

    /// <summary>
    /// Per-channel cache path so a <c>beta</c> check doesn't clobber the
    /// cached <c>latest</c> result (and vice versa).
    /// </summary>
    static string CachePathFor(string channel) =>
        PathHelpers.ConfigPath($"update-check-{channel}.json");

    /// <summary>
    /// On-disk shape of a per-channel update-check cache file. Two kinds of
    /// record share the file: a <b>success</b> record (<see cref="CheckedAt"/>
    /// set, <see cref="Failed"/> false) and a <b>backoff</b> record written
    /// after a failed/cancelled fetch (<see cref="AttemptedAt"/> set,
    /// <see cref="Failed"/> true) that still retains the last known
    /// <see cref="LatestVersion"/> so a transient registry outage doesn't
    /// regress an already-known "update available" result.
    /// </summary>
    /// <remarks>
    /// <see cref="Parse"/> reads the legacy two-field shape
    /// (<c>{latest_version, checked_at}</c>, written before backoff support
    /// existed) as a plain success record — the missing <see cref="AttemptedAt"/>/
    /// <see cref="Failed"/> fields default to null/false. That backward
    /// compatibility is deliberate: an existing on-disk cache from an older
    /// build must not be treated as corrupt.
    /// </remarks>
    internal sealed record UpdateCacheRecord(
        string? LatestVersion,
        DateTimeOffset? CheckedAt,
        DateTimeOffset? AttemptedAt,
        bool Failed) {

        /// <summary>
        /// Parses a cache file's JSON. Returns null for anything unparseable
        /// (corrupt/truncated file) so the caller can fall through to a fresh
        /// fetch rather than throw.
        /// </summary>
        public static UpdateCacheRecord? Parse(string json) {
            try {
                var node = JsonNode.Parse(json);
                if (node is null) return null;

                var latestVersion = node["latest_version"]?.GetValue<string>();
                var checkedAt     = node["checked_at"]?.GetValue<DateTimeOffset>();
                var attemptedAt   = node["attempted_at"]?.GetValue<DateTimeOffset>();
                var failed        = node["failed"]?.GetValue<bool>() ?? false;

                return new UpdateCacheRecord(latestVersion, checkedAt, attemptedAt, failed);
            } catch {
                return null;
            }
        }

        public string ToJson() {
            var obj = new JsonObject {
                ["latest_version"] = LatestVersion,
                ["checked_at"]     = CheckedAt,
                ["attempted_at"]   = AttemptedAt,
                ["failed"]         = Failed,
            };

            return obj.ToJsonString();
        }

        /// <summary>True for a successful check still inside the cache TTL (24h in production).</summary>
        public bool IsFresh(DateTimeOffset now, TimeSpan ttl) =>
            !Failed && LatestVersion is not null && CheckedAt is not null && now - CheckedAt.Value < ttl;

        /// <summary>
        /// True for a failed/cancelled check still inside its backoff window
        /// (1h in production) — the window during which a passive check
        /// skips the network entirely and serves the retained
        /// <see cref="LatestVersion"/> (which may itself be null if no check
        /// has ever succeeded).
        /// </summary>
        public bool InFailureBackoff(DateTimeOffset now, TimeSpan backoff) =>
            Failed && AttemptedAt is not null && now - AttemptedAt.Value < backoff;
    }

    /// <summary>Result of <see cref="CheckForUpdateAsync"/>.</summary>
    /// <param name="FromCache">
    /// True when <paramref name="Latest"/> came from a cached/retained value
    /// (a fresh success record, or a retained version served during failure
    /// backoff) rather than a network round-trip that just completed.
    /// </param>
    internal sealed record UpdateCheckResult(string? Current, string? Latest, bool Newer, bool FromCache);

    static readonly TimeSpan CacheTtl       = TimeSpan.FromHours(24);
    static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);

    /// <param name="ct">
    /// Bounds the network fetch. <c>forceCheck</c> callers pass
    /// <see cref="CancellationToken.None"/> and rely on the 5s
    /// <see cref="HttpClient.Timeout"/> below; passive callers pass a short
    /// (~3s) token so an unresponsive registry can't stall every CLI
    /// invocation. Never used for the cache write itself — see
    /// <see cref="WriteCacheRecordAsync"/>.
    /// </param>
    internal static async Task<UpdateCheckResult> CheckForUpdateAsync(bool forceCheck, string channel, CancellationToken ct = default) {
        var current   = GetCurrentVersion();
        var cachePath = CachePathFor(channel);
        var now       = DateTimeOffset.UtcNow;

        UpdateCacheRecord? cached = null;
        if (File.Exists(cachePath)) {
            try {
                // Never bound this local read by the (possibly short, passive)
                // request token — only the network fetch below should be.
                cached = UpdateCacheRecord.Parse(await File.ReadAllTextAsync(cachePath, CancellationToken.None));
            } catch {
                // Corrupted/unreadable cache file — fall through to a fresh fetch.
            }
        }

        if (!forceCheck && cached is not null
         && (cached.IsFresh(now, CacheTtl) || cached.InFailureBackoff(now, FailureBackoff))) {
            // Either a fresh success record, or a still-backed-off failure
            // record — both cases serve the retained LatestVersion (which is
            // null only if no check has ever succeeded) without touching the
            // network.
            return new UpdateCheckResult(current, cached.LatestVersion, IsNewer(cached.LatestVersion, current), FromCache: true);
        }

        // Query npm registry.
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        http.DefaultRequestHeaders.Add("User-Agent", "kcap-cli");

        try {
            var resp = await http.GetAsync($"{RegistryBaseUrl}/@kurrent/kcap/{channel}", ct);

            if (!resp.IsSuccessStatusCode) {
                await WriteBackoffRecordAsync(cachePath, cached?.LatestVersion, now);
                return new UpdateCheckResult(current, cached?.LatestVersion, IsNewer(cached?.LatestVersion, current), FromCache: true);
            }

            var body   = await resp.Content.ReadAsStringAsync(ct);
            var json   = JsonNode.Parse(body);
            var latest = json?["version"]?.GetValue<string>();

            if (latest is not null) {
                await WriteCacheRecordAsync(cachePath, new UpdateCacheRecord(latest, now, AttemptedAt: null, Failed: false));
            }

            return new UpdateCheckResult(current, latest, IsNewer(latest, current), FromCache: false);
        } catch {
            // Network failure, non-2xx handled above, or cancellation (either
            // the passive ct bound or the 5s HttpClient.Timeout). Pin a 1h
            // backoff so a wedged/slow registry isn't re-hit on every
            // invocation, but keep serving the last version we successfully
            // saw (stale-while-backoff).
            await WriteBackoffRecordAsync(cachePath, cached?.LatestVersion, now);
            return new UpdateCheckResult(current, cached?.LatestVersion, IsNewer(cached?.LatestVersion, current), FromCache: true);
        }
    }

    static Task WriteBackoffRecordAsync(string cachePath, string? retainedLatestVersion, DateTimeOffset attemptedAt) =>
        WriteCacheRecordAsync(cachePath, new UpdateCacheRecord(retainedLatestVersion, CheckedAt: null, attemptedAt, Failed: true));

    /// <summary>
    /// Atomic tmp+<see cref="File.Move(string, string, bool)"/> write, same
    /// pattern the rest of the config store uses. Deliberately never passed
    /// the request's own <see cref="CancellationToken"/> — a cancelled fetch
    /// must still be able to persist its backoff record — and never lets a
    /// filesystem failure propagate: a best-effort cache write must not turn
    /// an otherwise-successful check into a reported failure.
    /// </summary>
    static async Task WriteCacheRecordAsync(string cachePath, UpdateCacheRecord record) {
        try {
            var dir = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(dir);

            var tempPath = $"{cachePath}.tmp";
            await File.WriteAllTextAsync(tempPath, record.ToJson(), CancellationToken.None);
            File.Move(tempPath, cachePath, overwrite: true);
        } catch {
            // Best-effort — never fail the check because the cache write did.
        }
    }

    static bool IsNewer(string? latest, string? current) => PrereleaseSemver.IsNewer(latest, current);

    static string? GetCurrentVersion() {
        var v = CapacitorVersion.CurrentDisplay();
        return v == "unknown" ? null : v;
    }
}
