using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Tests.Unit;

public class MutationModelTests {
    // ---- ForStartGate (spec §3/§4 pinned table) ----

    [Test]
    [Arguments("directive_missing", RecoverySurface.Takeover)]
    [Arguments("directive_invalid", RecoverySurface.Takeover)]
    [Arguments("identity_mismatch", RecoverySurface.Takeover)]
    [Arguments("foreign_binary", RecoverySurface.Takeover)]
    [Arguments("package_inconsistent", RecoverySurface.Reinstall)]
    [Arguments("evidence_unreadable", RecoverySurface.Attention)]
    [Arguments("some_unrecognized_token", RecoverySurface.Attention)]
    public async Task ForStartGate_routes_per_pinned_table(string token, RecoverySurface expected) {
        await Assert.That(ReasonRouting.ForStartGate(token)).IsEqualTo(expected);
    }

    // ---- ForDaemonStart (spec §3/§4 pinned table) ----

    [Test]
    [Arguments("package_inconsistent", RecoverySurface.Reinstall)]
    [Arguments("some_unrecognized_token", RecoverySurface.Attention)]
    public async Task ForDaemonStart_routes_per_pinned_table(string token, RecoverySurface expected) {
        await Assert.That(ReasonRouting.ForDaemonStart(token)).IsEqualTo(expected);
    }

    // ---- ForBootRefusal (spec §3/§4 pinned table) ----

    [Test]
    [Arguments("server_expectation_mismatch", RecoverySurface.Takeover)]
    [Arguments("consent_seed_unwritable", RecoverySurface.Storage)]
    [Arguments("consent_seed_invalid", RecoverySurface.Takeover)]
    [Arguments("some_unrecognized_token", RecoverySurface.Attention)]
    public async Task ForBootRefusal_routes_per_pinned_table(string token, RecoverySurface expected) {
        await Assert.That(ReasonRouting.ForBootRefusal(token)).IsEqualTo(expected);
    }
}
