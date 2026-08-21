using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Canonicalizes a DeepSeek Harness session id to the system's ≤36-char, GUID-shaped
/// session-id contract (AI-2020). dsh names sessions <c>session-&lt;guid&gt;</c> /
/// <c>main-session-&lt;guid&gt;</c> (44–49 chars), which the read model's pervasive
/// <c>length(session_id) &lt;= 36</c> guard filters out of every session query. We extract
/// the embedded GUID as its dashless ("N") form (32 chars) so a dsh session keys exactly
/// like Claude/Codex/Cursor and lists everywhere.
///
/// Applied identically on the live-hook and import paths so the transcript and lifecycle
/// converge on one stream. Ids already ≤36 with no embedded GUID (e.g. the offline PoC
/// <c>kcap-live-poc-1</c>) pass through unchanged; any other over-length id without a GUID
/// falls back to a stable 32-char hash so the contract always holds.
/// </summary>
public static class DshSessionId {
    public static string Canonicalize(string rawId) {
        if (string.IsNullOrEmpty(rawId)) return rawId;

        // Bare GUID (dashed or dashless) → dashless.
        if (Guid.TryParse(rawId, out var whole)) return whole.ToString("N");

        // dsh "<prefix>-<guid>": the GUID is the trailing 36 chars.
        if (rawId.Length >= 36 && Guid.TryParse(rawId[^36..], out var tail)) return tail.ToString("N");

        // Short, non-GUID ids already satisfy the contract (PoC / synthetic ids).
        if (rawId.Length <= 36) return rawId;

        // Over-length with no extractable GUID: stable, deterministic 32-char hash.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawId));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
