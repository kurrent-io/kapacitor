using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The per-launch names are the whole of an aliasing vendor's MCP containment: the gate is an exact-name
/// match, so a predictable or reused name lets the repository being worked in name its own server to match
/// and get a process spawned as the daemon user.
///
/// <para>These assert the CONSTRUCTION rules, not the record's shape. A record with the right three
/// properties and a degraded generator would satisfy every argv-equality test in the suite while reopening
/// the hole, which is why construction is private and these tests exist.</para>
/// </summary>
public class LaunchIdentityTests {
    static Guid A => Guid.Parse("11111111-1111-1111-1111-111111111111");
    static Guid B => Guid.Parse("22222222-2222-2222-2222-222222222222");
    static Guid C => Guid.Parse("33333333-3333-3333-3333-333333333333");

    const string AHex = "11111111111111111111111111111111";
    const string BHex = "22222222222222222222222222222222";
    const string CHex = "33333333333333333333333333333333";

    // ── the aliasing vendor (Gemini today) ──

    [Test]
    public async Task Aliasing_WireNameIsTheCanonicalIdSuffixedWithExactlyTheChannelGuid() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: true);

        // Exact equality against a value the test chose. A derived generator — a hash of a session id, a
        // counter, a clock — would produce a GUID-shaped string that passes uniqueness and format checks
        // but not this one.
        await Assert.That(id.ResultChannelWireName)
            .IsEqualTo($"{KcapMcpRegistry.ReservedResultChannelId}-{AHex}");
    }

    [Test]
    public async Task UnmatchableName_IsExactlyTheDenyPrefixAndTheOtherGuid() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: true);

        await Assert.That(id.UnmatchableMcpName).IsEqualTo($"kcap-deny-{BHex}");
    }

    /// <summary>An allowlist server rides the wire under the canonical id plus the launch's allowlist
    /// suffix — a canonical id alone is a fixed public literal the reviewed repository could declare its
    /// own server under, and the vendor's gate matches names exactly.</summary>
    [Test]
    public async Task Aliasing_AllowlistWireNameIsTheCanonicalIdSuffixedWithExactlyTheAllowlistGuid() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: true);

        await Assert.That(id.AllowlistWireName("kcap-review")).IsEqualTo($"kcap-review-{CHex}");
    }

    /// <summary>Reserved-name comparisons must keep resolving on the canonical id, or the registry's
    /// reservation check and Copilot's tool-id builder start disagreeing with what the vendor was sent.</summary>
    [Test]
    public async Task Aliasing_CanonicalIdIsTheReservedChannelId_NotTheWireName() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: true);

        await Assert.That(id.ResultChannelCanonicalId).IsEqualTo(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(id.ResultChannelCanonicalId).IsNotEqualTo(id.ResultChannelWireName);
    }

    // ── every other vendor: byte-identical to before this type existed ──

    /// <summary>
    /// The regression guard for Cursor, Copilot and Kiro. Aliasing is opt-in per vendor; applying it
    /// everywhere would change three shipped reviewers' wire behaviour as a side effect of a Gemini change.
    /// </summary>
    [Test]
    public async Task NonAliasing_WireNameEqualsTheCanonicalId() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: false);

        await Assert.That(id.ResultChannelWireName).IsEqualTo(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(id.ResultChannelWireName).IsEqualTo(id.ResultChannelCanonicalId);
    }

    /// <summary>Same guard for the allowlist names: a non-aliasing vendor's injected servers keep their
    /// canonical ids on the wire, which is what Copilot's tool-id builder and additional-mcp-config key on.</summary>
    [Test]
    public async Task NonAliasing_AllowlistWireNameReturnsTheCanonicalIdUnchanged() {
        var id = LaunchIdentity.FromGuids(A, B, C, aliasResultChannel: false);

        await Assert.That(id.AllowlistWireName("kcap-review")).IsEqualTo("kcap-review");
        await Assert.That(id.AllowlistWireName("kcap-review-context")).IsEqualTo("kcap-review-context");
    }

    /// <summary>The unmatchable name is per-launch for EVERY vendor — that behaviour predates aliasing and
    /// must not become conditional on it.</summary>
    [Test]
    public async Task NonAliasing_StillGetsAFreshUnmatchableName() {
        var a = LaunchIdentity.ForLaunch(aliasResultChannel: false);
        var b = LaunchIdentity.ForLaunch(aliasResultChannel: false);

        await Assert.That(a.UnmatchableMcpName).IsNotEqualTo(b.UnmatchableMcpName);
        await Assert.That(a.UnmatchableMcpName).StartsWith("kcap-deny-");
    }

    // ── properties both arms must hold ──

    /// <summary>
    /// Every name carries into an argv option whose value the vendor comma-splits, so a comma in any would
    /// silently become two allowlist entries. A GUID hex cannot contain one — asserted rather than assumed,
    /// because it is the reason the vendor's coercion semantics never have to be reproduced.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task NoNameCanCarryACommaOrWhitespace(bool alias) {
        var id = LaunchIdentity.ForLaunch(alias);

        foreach (var name in new[] {
                     id.ResultChannelWireName, id.UnmatchableMcpName, id.AllowlistWireName("kcap-review") }) {
            await Assert.That(name).DoesNotContain(",");
            await Assert.That(name.Any(char.IsWhiteSpace)).IsFalse();
        }
    }

    [Test]
    public async Task Aliasing_ForLaunchProducesAFreshWireNameEveryTime() {
        var a = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        var b = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        await Assert.That(a.ResultChannelWireName).IsNotEqualTo(b.ResultChannelWireName);
        await Assert.That(a.UnmatchableMcpName).IsNotEqualTo(b.UnmatchableMcpName);
        await Assert.That(a.AllowlistWireName("kcap-review")).IsNotEqualTo(b.AllowlistWireName("kcap-review"));
    }

    /// <summary>
    /// The three names must not be derivable from one another: the agent can read its own argv, so learning
    /// one generated suffix must not yield another.
    /// </summary>
    [Test]
    public async Task ForLaunch_UsesIndependentGuidsForTheThreeNames() {
        var id = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        var channelGuid   = id.ResultChannelWireName[(KcapMcpRegistry.ReservedResultChannelId.Length + 1)..];
        var denyGuid      = id.UnmatchableMcpName["kcap-deny-".Length..];
        var allowlistGuid = id.AllowlistWireName("kcap-review")["kcap-review-".Length..];

        await Assert.That(channelGuid).IsNotEqualTo(denyGuid);
        await Assert.That(channelGuid).IsNotEqualTo(allowlistGuid);
        await Assert.That(denyGuid).IsNotEqualTo(allowlistGuid);
    }

    [Test]
    [Arguments("00000000-0000-0000-0000-000000000000", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333")]
    [Arguments("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000", "33333333-3333-3333-3333-333333333333")]
    [Arguments("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "00000000-0000-0000-0000-000000000000")]
    public async Task AnEmptyGuid_IsRefused(string channel, string unmatchable, string allowlist) {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LaunchIdentity.FromGuids(Guid.Parse(channel), Guid.Parse(unmatchable), Guid.Parse(allowlist), true));

        await Assert.That(ex!.Message).Contains("predictable");
    }

    /// <summary>A refactor sharing one GUID between any two names would pass every format assertion.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReusingOneGuidAcrossNames_IsRefused(bool alias) {
        foreach (var (channel, unmatchable, allowlist) in new[] { (A, A, C), (A, B, A), (A, B, B) }) {
            var ex = Assert.Throws<InvalidOperationException>(
                () => LaunchIdentity.FromGuids(channel, unmatchable, allowlist, alias));

            await Assert.That(ex!.Message).Contains("reused");
        }
    }
}
