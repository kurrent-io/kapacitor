using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Tests.Unit;

public class DshSessionIdTests {
    [Test]
    [Arguments("session-e1d79e8a-9b62-4b23-b576-7e7493c09dba",      "e1d79e8a9b624b23b5767e7493c09dba")] // session-<guid>
    [Arguments("main-session-3bc87030-2c61-4183-9711-358334dd48d3", "3bc870302c6141839711358334dd48d3")] // main-session-<guid>
    [Arguments("e1d79e8a-9b62-4b23-b576-7e7493c09dba",              "e1d79e8a9b624b23b5767e7493c09dba")] // bare dashed guid
    [Arguments("e1d79e8a9b624b23b5767e7493c09dba",                  "e1d79e8a9b624b23b5767e7493c09dba")] // bare dashless guid
    [Arguments("kcap-live-poc-1",                                   "kcap-live-poc-1")]                  // short non-guid passthrough
    public async Task Canonicalize_extracts_guid_or_passes_through(string raw, string expected) {
        await Assert.That(DshSessionId.Canonicalize(raw)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("session-e1d79e8a-9b62-4b23-b576-7e7493c09dba")]
    [Arguments("main-session-3bc87030-2c61-4183-9711-358334dd48d3")]
    [Arguments("an-absurdly-long-non-guid-id-that-exceeds-the-thirty-six-character-contract")]
    public async Task Canonicalize_always_satisfies_the_36_char_contract(string raw) {
        await Assert.That(DshSessionId.Canonicalize(raw).Length).IsLessThanOrEqualTo(36);
    }
}
