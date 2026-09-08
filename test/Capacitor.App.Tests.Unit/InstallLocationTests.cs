using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationTests {
    const string Home = "/Users/dev";

    [Test]
    public async Task BundleRoot_is_the_app_ancestor_of_the_executable() {
        await Assert.That(InstallLocation.BundleRoot("/Applications/Kurrent Capacitor.app/Contents/MacOS/Kurrent Capacitor"))
            .IsEqualTo("/Applications/Kurrent Capacitor.app");
    }

    [Test]
    [Arguments("/Users/dev/src/kcap-cli/src/Capacitor.App/bin/Debug/net10.0/Kurrent Capacitor")]
    [Arguments("")]
    [Arguments(null)]
    public async Task BundleRoot_is_null_outside_a_bundle(string? path) {
        await Assert.That(InstallLocation.BundleRoot(path)).IsNull();
    }

    [Test]
    [Arguments("/Applications/Kurrent Capacitor.app", InstallLocationKind.Applications)]
    [Arguments("/Users/dev/Applications/Kurrent Capacitor.app", InstallLocationKind.UserApplications)]
    [Arguments("/Volumes/Kurrent Capacitor/Kurrent Capacitor.app", InstallLocationKind.DmgVolume)]
    [Arguments("/private/var/folders/xy/T/AppTranslocation/1F2E-3D4C/d/Kurrent Capacitor.app", InstallLocationKind.Translocated)]
    [Arguments("/Users/dev/Downloads/Kurrent Capacitor.app", InstallLocationKind.Other)]
    [Arguments(null, InstallLocationKind.NotABundle)]
    public async Task Classify_recognises_each_shape(string? root, InstallLocationKind expected) {
        await Assert.That(InstallLocation.Classify(root, Home)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(InstallLocationKind.NotABundle, true)]
    [Arguments(InstallLocationKind.Applications, true)]
    [Arguments(InstallLocationKind.UserApplications, true)]
    [Arguments(InstallLocationKind.DmgVolume, false)]
    [Arguments(InstallLocationKind.Translocated, false)]
    [Arguments(InstallLocationKind.Other, false)]
    public async Task Passes_only_for_installed_or_unbundled(InstallLocationKind kind, bool expected) {
        await Assert.That(InstallLocation.Passes(kind)).IsEqualTo(expected);
    }
}
