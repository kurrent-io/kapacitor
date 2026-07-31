using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The two per-launch names are the whole of the Gemini reviewer's containment: the vendor's MCP allowlist is
/// an exact-name gate, so a predictable or reused name lets the repository under review name its own server
/// to match and get a process spawned as the daemon user.
///
/// <para>These assert the CONSTRUCTION rules, not the record's shape. A record with the right three
/// properties and a degraded generator would satisfy every argv-equality test in the suite while reopening
/// the hole, which is why construction is private and these tests exist.</para>
/// </summary>
public class GeminiLaunchIdentityTests {
    static Guid A => Guid.Parse("11111111-1111-1111-1111-111111111111");
    static Guid B => Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task WireName_IsTheCanonicalIdSuffixedWithExactlyTheChannelGuid() {
        var id = GeminiLaunchIdentity.FromGuids(A, B);

        // Exact equality against a value the test chose. A derived generator (a hash of a session id, a
        // counter, a clock) would produce a GUID-shaped string that passes uniqueness and format checks but
        // not this one.
        await Assert.That(id.WireName)
            .IsEqualTo($"{KcapMcpRegistry.ReservedResultChannelId}-11111111111111111111111111111111");
    }

    [Test]
    public async Task DenyAllName_IsExactlyTheDenyPrefixAndTheOtherGuid() {
        var id = GeminiLaunchIdentity.FromGuids(A, B);

        await Assert.That(id.DenyAllName).IsEqualTo("kcap-deny-22222222222222222222222222222222");
    }

    /// <summary>Reserved-name comparisons must keep resolving on the canonical id, or Copilot's tool-id
    /// builder and the registry's reservation check start disagreeing with what Gemini was sent.</summary>
    [Test]
    public async Task CanonicalId_IsTheReservedChannelId_NotTheWireName() {
        var id = GeminiLaunchIdentity.FromGuids(A, B);

        await Assert.That(id.CanonicalId).IsEqualTo(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(id.CanonicalId).IsNotEqualTo(id.WireName);
    }

    /// <summary>
    /// Both names carry into an argv option whose value the vendor comma-splits, so a comma in either would
    /// silently become two allowlist entries. A GUID hex cannot contain one — asserted rather than assumed,
    /// because it is the reason the vendor's coercion semantics never have to be reproduced.
    /// </summary>
    [Test]
    public async Task NeitherNameCanCarryACommaOrWhitespace() {
        var id = GeminiLaunchIdentity.ForLaunch();

        foreach (var name in new[] { id.WireName, id.DenyAllName }) {
            await Assert.That(name).DoesNotContain(",");
            await Assert.That(name.Any(char.IsWhiteSpace)).IsFalse();
        }
    }

    [Test]
    public async Task ForLaunch_ProducesAFreshWireNameEveryTime() {
        var a = GeminiLaunchIdentity.ForLaunch();
        var b = GeminiLaunchIdentity.ForLaunch();

        await Assert.That(a.WireName).IsNotEqualTo(b.WireName);
        await Assert.That(a.DenyAllName).IsNotEqualTo(b.DenyAllName);
    }

    /// <summary>
    /// The two names must not be derivable from each other: the reviewer can read its own argv, so learning
    /// the wire name must not yield the deny-all name.
    /// </summary>
    [Test]
    public async Task ForLaunch_UsesIndependentGuidsForTheTwoNames() {
        var id = GeminiLaunchIdentity.ForLaunch();

        var channelGuid = id.WireName[(KcapMcpRegistry.ReservedResultChannelId.Length + 1)..];
        var denyGuid    = id.DenyAllName["kcap-deny-".Length..];

        await Assert.That(channelGuid).IsNotEqualTo(denyGuid);
    }

    [Test]
    [Arguments("00000000-0000-0000-0000-000000000000", "22222222-2222-2222-2222-222222222222")]
    [Arguments("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000")]
    public async Task AnEmptyGuid_IsRefused(string channel, string deny) {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GeminiLaunchIdentity.FromGuids(Guid.Parse(channel), Guid.Parse(deny)));

        await Assert.That(ex!.Message).Contains("predictable");
    }

    /// <summary>A refactor sharing one GUID between the two names would pass every format assertion.</summary>
    [Test]
    public async Task ReusingOneGuidForBothNames_IsRefused() {
        var ex = Assert.Throws<InvalidOperationException>(() => GeminiLaunchIdentity.FromGuids(A, A));

        await Assert.That(ex!.Message).Contains("reused");
    }
}
