using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// The pairing channel hands over a human's approval and never a credential, so nothing but this
// comparison binds the person who approved to the identity that then authenticates.
public class PairingIdentityTests {
    // A JWT the CLI was handed. Only the payload is read — this is not, and must not be read as, a
    // validation: the server re-checks the same comparison at /complete.
    static string Jwt(params (string Name, string Value)[] claims) =>
        $"{Segment("{\"alg\":\"none\"}")}.{Segment(Payload(claims))}.signature";

    static string Payload((string Name, string Value)[] claims) =>
        "{" + string.Join(",", claims.Select(c => $"{JsonSerializer.Serialize(c.Name)}:{JsonSerializer.Serialize(c.Value)}")) + "}";

    static string Segment(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The server stamps kapacitor:user_id into the GitHub JWT it mints, and sub carries a username —
    // so preferring sub would compare a name against a canonical id and never match.
    [Test]
    public async Task The_canonical_claim_wins_over_sub() =>
        await Assert.That(PairingIdentity.FromAccessToken(
            Jwt(("sub", "octocat"), ("kapacitor:user_id", "github:4242")))).IsEqualTo("github:4242");

    [Test]
    public async Task WorkOS_falls_back_to_sub() =>
        await Assert.That(PairingIdentity.FromAccessToken(Jwt(("sub", "user_01J9")))).IsEqualTo("user_01J9");

    // A pre-rekey token carries only the legacy numeric claim, and the server normalises it the same
    // way — a raw compare would false-mismatch the same person.
    [Test]
    public async Task A_legacy_numeric_claim_normalises_to_a_canonical_id() =>
        await Assert.That(PairingIdentity.FromAccessToken(
            Jwt(("sub", "octocat"), ("kapacitor:github_id", "4242")))).IsEqualTo("github:4242");

    [Test]
    public async Task Normalize_leaves_an_already_canonical_id_alone() {
        await Assert.That(PairingIdentity.Normalize("github:7")).IsEqualTo("github:7");
        await Assert.That(PairingIdentity.Normalize("user_01J9")).IsEqualTo("user_01J9");
        await Assert.That(PairingIdentity.Normalize("7")).IsEqualTo("github:7");
    }

    [Test]
    public async Task A_token_with_no_identity_claim_yields_nothing() =>
        await Assert.That(PairingIdentity.FromAccessToken(Jwt(("iss", "kcap")))).IsNull();

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-a-jwt")]
    [Arguments("only.two")]
    [Arguments("header.!!!not-base64!!!.sig")]
    public async Task A_malformed_token_yields_nothing(string token) =>
        await Assert.That(PairingIdentity.FromAccessToken(token)).IsNull();

    [Test]
    public async Task The_same_identity_matches() =>
        await Assert.That(PairingIdentity.Matches("user_01J9", Jwt(("sub", "user_01J9")))).IsTrue();

    [Test]
    public async Task A_legacy_expectation_matches_its_canonical_token() =>
        await Assert.That(PairingIdentity.Matches(
            "4242", Jwt(("kapacitor:user_id", "github:4242")))).IsTrue();

    // The whole point: the approving human and the authenticating one are different people.
    [Test]
    public async Task A_different_identity_does_not_match() =>
        await Assert.That(PairingIdentity.Matches("user_01J9", Jwt(("sub", "user_09XX")))).IsFalse();

    // Fails closed. An id the CLI could not determine is not evidence that it matches, and an absent
    // expectation means the response was not the one this check was written against.
    [Test]
    [Arguments(null, "token")]
    [Arguments("", "token")]
    [Arguments("user_01J9", null)]
    [Arguments("user_01J9", "")]
    [Arguments("user_01J9", "garbage")]
    public async Task An_undeterminable_identity_never_matches(string? expected, string? token) =>
        await Assert.That(PairingIdentity.Matches(expected, token)).IsFalse();
}
