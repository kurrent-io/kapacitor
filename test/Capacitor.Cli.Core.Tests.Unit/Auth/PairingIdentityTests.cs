using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// The pairing channel hands over a human's approval and never a credential, so nothing but this
// comparison binds the person who approved to the identity that then authenticates.
public class PairingIdentityTests {
    static string Jwt(params (string Name, string Value)[] claims) =>
        $"{Segment("{\"alg\":\"none\"}")}.{Segment(Payload(claims))}.signature";

    static string Payload((string Name, string Value)[] claims) =>
        "{" + string.Join(",", claims.Select(c => $"{JsonSerializer.Serialize(c.Name)}:{JsonSerializer.Serialize(c.Value)}")) + "}";

    static string Segment(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── THE TOKENS SERVERS ACTUALLY MINT ──
    //
    // Copied from the server's own token construction, NOT invented. An earlier version of this file
    // synthesised `kapacitor:user_id` claims and passed, while the real GitHub token — which carries
    // github_id and a `github|{n}` subject — failed the comparison for every user on every GitHub
    // tenant. A fixture that encodes an assumption only ever tests the assumption.

    // AuthEndpoints mints: github_id, github_username, sub = "github|{n}".
    static string GitHubToken(long id) =>
        Jwt(("github_id", id.ToString(CultureInfo.InvariantCulture)), ("github_username", "octocat"), ("sub", $"github|{id}"));

    // WorkOS AuthKit: sub IS the canonical id, used verbatim by the server's claims transformation.
    static string WorkOSToken(string userId) => Jwt(("sub", userId));

    [Test]
    public async Task A_real_github_token_yields_the_id_the_server_would_compare() =>
        await Assert.That(PairingIdentity.FromAccessToken(GitHubToken(4242))).IsEqualTo("github:4242");

    [Test]
    public async Task A_real_workos_token_yields_its_subject() =>
        await Assert.That(PairingIdentity.FromAccessToken(WorkOSToken("user_01J9"))).IsEqualTo("user_01J9");

    // The end-to-end shape of the check: approved_by comes off the server as github:{n}.
    [Test]
    public async Task A_github_approver_matches_its_own_token() =>
        await Assert.That(PairingIdentity.Compare("github:4242", GitHubToken(4242)))
            .IsEqualTo(PairingContinuity.Match);

    [Test]
    public async Task A_workos_approver_matches_its_own_token() =>
        await Assert.That(PairingIdentity.Compare("user_01J9", WorkOSToken("user_01J9")))
            .IsEqualTo(PairingContinuity.Match);

    [Test]
    public async Task A_different_github_user_is_a_mismatch() =>
        await Assert.That(PairingIdentity.Compare("github:4242", GitHubToken(9999)))
            .IsEqualTo(PairingContinuity.Mismatch);

    [Test]
    public async Task A_different_workos_user_is_a_mismatch() =>
        await Assert.That(PairingIdentity.Compare("user_01J9", WorkOSToken("user_09XX")))
            .IsEqualTo(PairingContinuity.Mismatch);

    // ── NORMALISATION ──

    // The tenant writes the pipe into the token and the colon into approved_by, so comparing the two
    // raw is a guaranteed false mismatch for the same person.
    [Test]
    [Arguments("github:7", "github:7")]
    [Arguments("user_01J9", "user_01J9")]
    [Arguments("7", "github:7")]
    [Arguments("github|7", "github:7")]
    public async Task Normalize_maps_every_known_form_onto_the_canonical_one(string input, string expected) =>
        await Assert.That(PairingIdentity.Normalize(input)).IsEqualTo(expected);

    // A pre-rekey expectation still names the same person.
    [Test]
    public async Task A_legacy_numeric_expectation_matches_its_token() =>
        await Assert.That(PairingIdentity.Compare("4242", GitHubToken(4242)))
            .IsEqualTo(PairingContinuity.Match);

    // ── FAILING CLOSED, WITHOUT BLAMING THE USER ──

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

    // A valid JWT whose payload is not a JSON object. TryGetProperty THROWS on a non-object element,
    // so reading claims without an object guard takes setup down rather than declining to answer.
    [Test]
    [Arguments("[\"a\"]")]
    [Arguments("\"a string\"")]
    [Arguments("42")]
    [Arguments("null")]
    public async Task A_payload_that_is_not_an_object_yields_nothing(string payloadJson) {
        var token = $"{Segment("{\"alg\":\"none\"}")}.{Segment(payloadJson)}.signature";

        await Assert.That(PairingIdentity.FromAccessToken(token)).IsNull();
    }

    // "I could not tell" is not "it was somebody else": one means update the CLI, the other means a
    // colleague approved your machine. Reporting the first as the second sends people nowhere useful.
    [Test]
    [Arguments(null, "token")]
    [Arguments("", "token")]
    [Arguments("user_01J9", null)]
    [Arguments("user_01J9", "")]
    [Arguments("user_01J9", "garbage")]
    public async Task An_undeterminable_identity_is_indeterminate_not_a_mismatch(string? expected, string? token) =>
        await Assert.That(PairingIdentity.Compare(expected, token)).IsEqualTo(PairingContinuity.Indeterminate);
}
