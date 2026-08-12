using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// The effective "update available" advisory after capping the raw npm-latest target at the connected
/// server's version. A manually-rolled tenant can trail npm for days, so recommending a CLI newer than
/// the server it talks to only risks protocol mismatch; the passive update notice and <c>kcap status</c>
/// render this instead of the raw npm result. <see cref="ServerCapped"/> drives the pinned-install copy.
/// </summary>
internal readonly record struct UpdateAdvisory(string? Current, string? Target, bool Newer, bool ServerCapped);

internal static class UpdateAdvisoryResolver {
    /// <summary>Resolves the server URL the same way the authenticated HTTP choke point does
    /// (<c>HttpClientExtensions.CreateClientCoreImplAsync</c>), so the cached server version is read
    /// under the same key it was captured with.</summary>
    internal static string ResolvedServerUrl() =>
        AppConfig.ResolvedServerUrl ?? Environment.GetEnvironmentVariable("KCAP_URL") ?? "http://localhost:5108";

    /// <summary>Production entry: caps against the cached version for the current server.</summary>
    internal static UpdateAdvisory Resolve(UpdateCommand.UpdateCheckResult? result, string channel) =>
        Resolve(result, channel, ServerVersionStore.Get(ResolvedServerUrl()));

    /// <summary>
    /// Pure, testable core. The cap engages ONLY on the stable <c>latest</c> channel (a beta user
    /// deliberately rides ahead of the server) AND when a cached server version is present and parses as
    /// a stable release. Otherwise the raw npm result passes through unchanged — an old CLI, a
    /// never-connected server, or the beta channel keeps today's behaviour (the cold-start doctrine).
    /// When capping, the target is <c>min(npm latest, server version)</c> and "newer" is recomputed
    /// against it, so a user already at/ahead of their server is never nagged.
    /// </summary>
    internal static UpdateAdvisory Resolve(UpdateCommand.UpdateCheckResult? result, string channel, string? cachedServerVersion) {
        if (result is not { Latest: { } latest, Current: { } current })
            return new(result?.Current, result?.Latest, result?.Newer ?? false, ServerCapped: false);

        if (!string.Equals(channel, "latest", StringComparison.Ordinal)
                || cachedServerVersion is null
                || !IsStableRelease(cachedServerVersion))
            return new(current, latest, result.Newer, ServerCapped: false);

        // min(npm latest, server version): the server caps only when it is strictly older than npm latest.
        var capped = PrereleaseSemver.IsNewer(latest, cachedServerVersion);
        var target = capped ? cachedServerVersion : latest;

        return new(current, target, PrereleaseSemver.IsNewer(target, current), ServerCapped: capped);
    }

    /// <summary>A "stable release" parses as a dotted numeric core (≥3 components) with no prerelease
    /// suffix (build metadata is ignored). The server only ever sends stable versions in the header;
    /// this is belt-and-braces against an old or malformed cached value.</summary>
    internal static bool IsStableRelease(string version) {
        var s    = version.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (s.Contains('-')) return false; // prerelease suffix

        return Version.TryParse(s, out var v) && v.Build >= 0; // Build < 0 ⇒ fewer than 3 components
    }
}
