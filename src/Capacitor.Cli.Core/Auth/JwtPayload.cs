using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Reads the payload of a JWT the CLI was handed, without validating anything.
///
/// <para>Signature validation is the server's job against JWKS. Every caller here is asking about a
/// token it already holds — when does it expire, who is it — so there is nothing to be gained by
/// distrusting it and nothing being granted on the answer.</para>
/// </summary>
public static class JwtPayload {
    /// <summary>Null for anything that is not a readable JWT payload.</summary>
    public static JsonDocument? TryRead(string? token) {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');

        if (parts.Length < 2) return null;

        try {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');

            return JsonDocument.Parse(
                Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
        } catch (Exception e) when (e is FormatException or JsonException or DecoderFallbackException) {
            return null;
        }
    }
}
