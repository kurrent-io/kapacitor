using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class JwtExpiryTests {
    static string TokenWithExp(long exp) {
        var json = $"{{\"exp\":{exp}}}";
        var b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{b64}.signature";
    }

    [Test]
    public async Task Reads_exp_claim_from_workos_access_token() {
        var exp    = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var result = TokenStore.JwtExpiry(TokenWithExp(exp));

        await Assert.That(result.ToUnixTimeSeconds()).IsEqualTo(exp);
    }

    [Test]
    public async Task Malformed_token_falls_back_to_short_lifetime() {
        var now    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = TokenStore.JwtExpiry("not-a-jwt").ToUnixTimeSeconds();

        // ~5 minute conservative default.
        await Assert.That(result).IsGreaterThanOrEqualTo(now + 240);
        await Assert.That(result).IsLessThanOrEqualTo(now + 360);
    }
}
