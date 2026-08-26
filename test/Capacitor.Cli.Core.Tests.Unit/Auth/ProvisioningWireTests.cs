using System.Text.Json;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class ProvisioningWireTests {
    [Test]
    public async Task ProvisionRequest_serializes_camelCase_not_snake_case() {
        var json = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = "Acme Inc", Slug = "acme", Tier = "free" },
            CapacitorJsonContext.Default.ProvisionRequest);

        await Assert.That(json).Contains(@"""orgName""");
        await Assert.That(json).Contains(@"""slug""");
        await Assert.That(json).Contains(@"""tier""");
        await Assert.That(json).DoesNotContain("org_name");
    }

    [Test]
    public async Task ProvisionResponse_deserializes_camelCase_active_body() {
        var body = """{"slug":"acme","state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""";
        var resp = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.ProvisionResponse)!;

        await Assert.That(resp.State).IsEqualTo("active");
        await Assert.That(resp.Url).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(resp.WorkosOrgId).IsEqualTo("org_live");
    }

    [Test]
    public async Task AvailabilityResponse_deserializes_reason() {
        var resp = JsonSerializer.Deserialize(
            """{"available":false,"reason":"taken"}""",
            CapacitorJsonContext.Default.AvailabilityResponse)!;

        await Assert.That(resp.Available).IsFalse();
        await Assert.That(resp.Reason).IsEqualTo("taken");
    }

    // CapacitorJsonContext is globally SnakeCaseLower, so a bare property would ship as join_id
    // and kcap-web — which reads named fields — would silently never see it. Nothing errors.
    [Test]
    public async Task ProvisionRequest_serializes_joinId_camelCase_not_snake_case() {
        var json = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = "Acme Inc", Slug = "acme", JoinId = "abc123" },
            CapacitorJsonContext.Default.ProvisionRequest);

        await Assert.That(json).Contains(@"""joinId"":""abc123""");
        await Assert.That(json).DoesNotContain("join_id");
    }

    // Old server / new CLI, and every opted-out run: an absent key must be an absent member, not
    // an explicit null, so the body is what it always was.
    [Test]
    public async Task ProvisionRequest_omits_joinId_entirely_when_there_is_no_key() {
        var json = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = "Acme Inc", Slug = "acme" },
            CapacitorJsonContext.Default.ProvisionRequest);

        await Assert.That(json).DoesNotContain("joinId");
    }

    [Test]
    public async Task StatusResponse_deserializes_camelCase_workosOrgId() {
        var resp = JsonSerializer.Deserialize(
            """{"state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""",
            CapacitorJsonContext.Default.StatusResponse)!;

        await Assert.That(resp.State).IsEqualTo("active");
        await Assert.That(resp.WorkosOrgId).IsEqualTo("org_live");
    }
}
