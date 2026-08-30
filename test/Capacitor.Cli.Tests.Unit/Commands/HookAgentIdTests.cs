using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare: KCAP_AGENT_ID is read by two hook commands and inherited by spawned children.
[NotInParallel]
public class HookAgentIdTests {
    [Test]
    public async Task Unset_and_empty_are_null() {
        using (EnvScope.Exclusive("KCAP_AGENT_ID", null)) await Assert.That(HookAgentId.FromEnvironment()).IsNull();
        using (EnvScope.Exclusive("KCAP_AGENT_ID", "")) await Assert.That(HookAgentId.FromEnvironment()).IsNull();
    }

    [Test]
    public async Task Set_is_returned_verbatim() {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", "6ba7b8109dad11d180b400c04fd430c8");
        await Assert.That(HookAgentId.FromEnvironment()).IsEqualTo("6ba7b8109dad11d180b400c04fd430c8");
    }
}
