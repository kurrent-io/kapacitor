using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// Reads one string claim out of a JWT payload WITHOUT validating the signature — the server
/// validates via JWKS; a client only ever uses these values for display and row classification,
/// never for authorization.
public static class JwtClaims {
    public static string? TryGetString(string accessToken, string claimName) {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return null;

        try {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch {
                2 => payload + "==",
                3 => payload + "=",
                1 => throw new FormatException("truncated base64url"),
                _ => payload,
            };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty(claimName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }
    }
}
