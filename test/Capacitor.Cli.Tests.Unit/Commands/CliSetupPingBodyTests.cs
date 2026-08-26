using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The cli-setup ping body is hand-built on purpose, and these assertions are what keep it that
/// way. Routing it through a typed DTO would inherit <c>CapacitorJsonContext</c>'s global
/// SnakeCaseLower policy and serialise <c>cli_version</c>, silently breaking an endpoint that
/// works today — the mirror image of the reason <c>ProvisionRequest.JoinId</c> needs an explicit
/// attribute.
///
/// <para>So both literal names are pinned here: camelCase on the wire, while the same value is
/// <c>join_id</c> as a PostHog property. That asymmetry is the single most likely thing in this
/// feature to be implemented wrong, and nothing else would catch it.</para>
/// </summary>
public class CliSetupPingBodyTests {
    const string Key = "0123456789abcdef0123456789abcdef";

    [Test]
    public async Task Carries_both_literal_camelCase_names() {
        var body = SetupCommand.CliSetupPingBody("1.2.3", Key);

        await Assert.That(body).IsEqualTo($$"""{"cliVersion":"1.2.3","joinId":"{{Key}}"}""");
    }

    [Test]
    public async Task Omits_joinId_when_there_is_no_key() {
        var body = SetupCommand.CliSetupPingBody("1.2.3", null);

        await Assert.That(body).IsEqualTo("""{"cliVersion":"1.2.3"}""");
    }

    // The pre-existing shape for an unreadable assembly version, unchanged.
    [Test]
    public async Task Keeps_a_null_cliVersion_serialising_as_it_does_today() {
        var body = SetupCommand.CliSetupPingBody(null, null);

        await Assert.That(body).IsEqualTo("""{"cliVersion":null}""");
    }

    [Test]
    public async Task Never_emits_snake_case() {
        var body = SetupCommand.CliSetupPingBody("1.2.3", Key);

        await Assert.That(body).DoesNotContain("cli_version");
        await Assert.That(body).DoesNotContain("join_id");
    }

    // Both inputs are ours — an assembly version and 32 hex chars — so neither can carry a quote
    // that would break the literal. Pinned so that stays true if either source ever changes.
    [Test]
    public async Task Produces_parseable_json_for_both_shapes() {
        foreach (var body in new[] {
            SetupCommand.CliSetupPingBody("1.2.3", Key),
            SetupCommand.CliSetupPingBody(null, null),
        }) {
            var parsed = System.Text.Json.Nodes.JsonNode.Parse(body);
            await Assert.That(parsed).IsNotNull();
        }
    }
}
