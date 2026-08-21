namespace Capacitor.Cli.Core.Tests.Unit;

public class CapacitorVersionTests {
    [Test]
    public async Task Current_returns_assembly_informational_version() {
        var version = CapacitorVersion.Current();
        await Assert.That(version).IsNotNull();
        await Assert.That(version).IsNotEmpty();
    }

    [Test]
    public async Task Current_matches_AgentsSkillsInstaller_CurrentVersion() {
        // Regression: every installer's marker must contain the same version
        // string so a postinstall checking one marker doesn't see drift caused
        // by two installers stamping different version values for the same build.
        await Assert.That(CapacitorVersion.Current())
            .IsEqualTo(AgentsSkillsInstaller.CurrentVersion());
    }

    [Test]
    [Arguments("0.4.11+sha.abc1234", "0.4.11")]
    [Arguments("0.4.11", "0.4.11")]
    [Arguments("unknown", "unknown")]
    [Arguments("1.2.3-preview.4+build.5", "1.2.3-preview.4")]
    public async Task Display_strips_build_metadata(string input, string expected) {
        await Assert.That(CapacitorVersion.Display(input)).IsEqualTo(expected);
    }
}
