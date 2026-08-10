using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `kcap machine create` resolves the visibility PRINTED in its setup instructions from the
/// operator's own configuration instead of steering to private: an explicit --visibility flag wins,
/// else the active profile's default_visibility (what `kcap setup` wrote), else the product default
/// org_public for a machine with no profile. Each carries a provenance label so the printed
/// `kcap config set default_visibility ...` line says where its value came from — and `private`
/// only ever appears because the flag or the operator's own profile chose it, never as this
/// command's own suggestion.
/// </summary>
public class MachineCreateVisibilityTests {
    [Test]
    public async Task Flag_wins_and_is_labeled_as_flag() {
        var (value, provenance) = MachineCommand.ResolveCreateVisibility("private", "org_public");
        await Assert.That(value).IsEqualTo("private");
        await Assert.That(provenance).IsEqualTo("from --visibility");
    }

    [Test]
    public async Task Profile_default_is_inherited_and_labeled() {
        var (value, provenance) = MachineCommand.ResolveCreateVisibility(null, "org_public");
        await Assert.That(value).IsEqualTo("org_public");
        await Assert.That(provenance).IsEqualTo("your profile default");
    }

    [Test]
    public async Task Private_profile_default_is_honored_not_overridden() {
        var (value, provenance) = MachineCommand.ResolveCreateVisibility(null, "private");
        await Assert.That(value).IsEqualTo("private");
        await Assert.That(provenance).IsEqualTo("your profile default");
    }

    [Test]
    public async Task No_profile_falls_back_to_product_default() {
        var (value, provenance) = MachineCommand.ResolveCreateVisibility(null, null);
        await Assert.That(value).IsEqualTo("org_public");
        await Assert.That(provenance).IsEqualTo("product default");
    }
}
