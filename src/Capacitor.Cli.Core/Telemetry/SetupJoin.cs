using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The loopback browser's view of the join: build the URL the closing page should navigate to,
/// and accept whatever comes back afterwards. Kept as an interface so
/// <see cref="Auth.LoopbackBrowser"/> stays dumb and testable, and so a test can drive it
/// without touching the process-wide statics in <see cref="SetupJoin"/>.
/// </summary>
public interface ILoopbackJoin {
    string? FirstHopUrl(int port);

    /// <summary>
    /// True when this really was our return hop and its context was taken. False for anything
    /// else — and the caller must keep waiting on a false, because the browser has not arrived yet.
    /// <para>The return value is load-bearing, not informational: without it a listener cannot
    /// tell a genuine return from a stray local request, so one junk request would end the wait
    /// and the real browser would find a closed socket.</para>
    /// </summary>
    bool Accept(string query);
}

/// <summary>
/// One opaque correlation key per interactive auth run, published as a telemetry shared property
/// so every <c>cli_*</c> event for the run carries it. It exists because the web, the CLI,
/// kcap-web's Worker and kcap-server each stamp a different <c>distinct_id</c>, so one human is
/// recorded as four unrelated people; this key is what lets those records be matched at query
/// time, with nothing stored and no mapping table anywhere.
///
/// <para><b>It grants nothing.</b> Not a credential, not a session, not a capability, and never
/// accepted as authentication by anything. That is why a leak is not a breach — but it IS the
/// bridge token, and a log or a file holding it is a copy of the join. So it is never written to
/// disk and never logged, which takes TWO guards rather than one, because attaching it as a shared
/// property gives it two ways out of this process: the debug renderer prints a placeholder instead
/// of the value, and <see cref="TelemetrySpool"/> strips it from any event parked on disk after a
/// failed delivery. Both key off <see cref="PropertyName"/>. The second guard was missing at first
/// and the omission was invisible, because it only fires when the network is unreachable.</para>
///
/// <para>Minted only when telemetry is enabled, which collapses every opt-out route
/// (<c>KCAP_TELEMETRY=0</c>, <c>DO_NOT_TRACK</c>, the persisted flag, the app-spawn marker) into
/// one check: no key means no redirect and no <c>joinId</c> on any request, off by construction
/// rather than by four separate guards.</para>
/// </summary>
public static class SetupJoin {
    /// <summary>
    /// The property name the key travels under, shared by everything that has to recognise it.
    /// There are exactly two places the key could otherwise escape the process — the debug
    /// renderer in <see cref="CliTelemetry"/>, which prints a placeholder, and
    /// <see cref="TelemetrySpool"/>, which strips it before an undeliverable event is parked on
    /// disk. Both key off this constant so a third exit cannot be added without meeting them.
    /// </summary>
    public const string PropertyName = "join_id";

    static string? _current;
    static int     _consumed;

    /// <summary>The key for this run, or null when telemetry is off or nothing minted one.</summary>
    public static string? Current => _current;

    /// <summary>
    /// Mints the key and registers it as a telemetry shared property, so every event captured
    /// from here on carries it without any call site changing. Idempotent: a second call returns
    /// the existing key rather than rotating it mid-run. Returns null — and does nothing — when
    /// telemetry is disabled.
    /// </summary>
    public static string? Mint() {
        try {
            if (!CliTelemetry.Enabled) return null;
            if (_current is not null) return _current;

            // Same shape and minting discipline as MachineId/TelemetryDeviceId.
            _current = Guid.NewGuid().ToString("N");
            CliTelemetry.AddSharedProperty(PropertyName, _current);

            return _current;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// The URL the closing page navigates to. Reuses <see cref="ProvisioningEndpoint.Url"/> so
    /// <c>KCAP_SIGNUP_URL</c> retargets it at a local miniflare for testing.
    /// <para><c>p</c> is a port integer, never a URL: the far side builds the loopback address
    /// itself, so accepting one here would make a production web page redirect anywhere a caller
    /// named.</para>
    /// </summary>
    public static string? FirstHopUrl(int port) {
        try {
            return _current is null ? null : $"{ProvisioningEndpoint.Url}/api/cli/return?j={_current}&p={port}";
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Validates and merges the context the browser hands back. This is the trust boundary: that
    /// request arrives over plain HTTP on a loopback port, and ANY local process, browser tab or
    /// web page able to cause a GET there can reach it. So arrival proves nothing — only a key
    /// match does, and the key was minted before the page that carries it was ever rendered.
    ///
    /// <para>Then field by field, INDEPENDENTLY, because one malformed field must never discard
    /// the others: <c>v</c> is optional and must be <c>legacy</c>|<c>redesign</c> — absent is the
    /// COMMON case, since only visitors the experiment actually assigned have an arm at all — and
    /// each web id must be <c>[A-Za-z0-9_-]</c> of length 1–64.</para>
    ///
    /// <para>That character class is a fail-closed PRIVACY guarantee, not formatting. Once the
    /// signup form calls <c>identify(email)</c> the web person's own id IS an email address, and
    /// <c>@</c> and <c>.</c> are outside the class — so a future change that routed one here is
    /// dropped rather than importing an identifier we promised never to collect.</para>
    ///
    /// <para>Exactly one call can ever succeed. A mismatch is dropped in silence: nothing merged,
    /// nothing persisted, nothing sent, no error surfaced, and nothing in debug output either,
    /// since the value is attacker-chosen. A mismatch deliberately does NOT consume the one-shot —
    /// burning it would let any local process kill the bridge by racing a junk request, and
    /// guessing a 128-bit key inside the drain's few seconds is not a real threat.</para>
    /// </summary>
    public static bool Accept(string query) {
        try {
            if (_current is null) return false;
            if (Volatile.Read(ref _consumed) != 0) return false;

            var candidate = Param(query, "j");
            if (candidate is null || !KeyMatches(candidate)) return false;

            if (Interlocked.Exchange(ref _consumed, 1) != 0) return false;

            // Built first, merged once. This runs on the browser's background wait while the
            // foreground is still reporting sign-in, so three separate merges would let an event be
            // captured mid-set — one host's id present and the other's missing, which reads as a
            // real absence rather than a moment in between.
            var accepted = new JsonObject();

            if (Param(query, "v") is { } variant && variant is "legacy" or "redesign")
                accepted["site_variant"] = variant;

            if (Param(query, "w1") is { } capacitorId && IsWebId(capacitorId))
                accepted["web_device_id_capacitor"] = capacitorId;

            if (Param(query, "w2") is { } wwwId && IsWebId(wwwId))
                accepted["web_device_id_www"] = wwwId;

            CliTelemetry.AddSharedProperties(accepted);

            return true;
        } catch {
            return false;
        }
    }

    // Constant-time, so a local process probing the port cannot recover the key one byte at a
    // time from response timing.
    static bool KeyMatches(string candidate) {
        var expected = _current;
        if (expected is null || candidate.Length != expected.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
    }

    // The [A-Za-z0-9_-]{1,64} guarantee as a character walk: identical semantics to a regex,
    // with no regex engine in a NativeAOT binary.
    static bool IsWebId(string value) {
        if (value.Length is 0 or > 64) return false;

        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return false;

        return true;
    }

    // Four known fields, so a hand parse beats a dependency. Null when absent or empty.
    static string? Param(string query, string name) {
        foreach (var pair in query.TrimStart('?').Split('&')) {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(pair[..eq], name, StringComparison.Ordinal)) continue;

            var raw = pair[(eq + 1)..];

            return raw.Length == 0 ? null : Uri.UnescapeDataString(raw);
        }

        return null;
    }

    /// <summary>Test seam: restores the pristine, nothing-minted state.</summary>
    public static void Reset() {
        _current  = null;
        _consumed = 0;
    }

    /// <summary>The adapter handed to <see cref="Auth.LoopbackBrowser"/>; forwards to the statics.</summary>
    public static ILoopbackJoin Loopback { get; } = new StaticLoopbackJoin();

    sealed class StaticLoopbackJoin : ILoopbackJoin {
        public string? FirstHopUrl(int port) => SetupJoin.FirstHopUrl(port);
        public bool Accept(string query)     => SetupJoin.Accept(query);
    }
}
