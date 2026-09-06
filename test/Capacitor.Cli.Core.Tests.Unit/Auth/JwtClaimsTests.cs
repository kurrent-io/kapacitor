using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class JwtClaimsTests {
    static string Token(string payloadJson) {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64Url("{\"alg\":\"RS256\"}")}.{B64Url(payloadJson)}.sig";
    }

    [Test]
    public async Task ReadsAStringClaim() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"sub":"user_123","team_id":"t9"}"""), "sub"))
            .IsEqualTo("user_123");

    [Test]
    public async Task MissingClaimIsNull() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"sub":"user_123"}"""), "team_id")).IsNull();

    [Test]
    public async Task NonStringClaimIsNull() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"n":42}"""), "n")).IsNull();

    [Test]
    [Arguments("")]
    [Arguments("not-a-jwt")]
    [Arguments("a.b")]
    [Arguments("a.%%%.c")]
    public async Task GarbageIsNullNeverThrows(string token) =>
        await Assert.That(JwtClaims.TryGetString(token, "sub")).IsNull();

    [Test]
    public async Task PayloadNeedingBase64PaddingParses() {
        // A payload whose base64url length % 4 == 2 exercises the padding branch.
        var token = Token("""{"sub":"abc"}""");
        await Assert.That(JwtClaims.TryGetString(token, "sub")).IsEqualTo("abc");
    }

    [Test]
    [Arguments("42")]
    [Arguments("[1]")]
    [Arguments("null")]
    public async Task NonObjectPayloadIsNullNeverThrows(string payload) =>
        await Assert.That(JwtClaims.TryGetString(Token(payload), "sub")).IsNull();
}
