namespace Capacitor.Cli.Tests.Unit;

/// The bundle test is a pure path shape plus one file probe; separators are compared with
/// EndsWith so the same cases hold on the Windows CI leg.
public class InstallProvenanceTests {
    static bool PlistExists(string p) => p.Replace('\\', '/').EndsWith("/Contents/Info.plist", StringComparison.Ordinal);

    [Test]
    public async Task Inside_a_bundle_with_a_plist_is_bundled() {
        var bundled = InstallProvenance.IsAppBundled("/Applications/Kurrent Capacitor.app/Contents/MacOS/kcap", PlistExists);

        await Assert.That(bundled).IsTrue();
    }

    [Test]
    public async Task Bundle_shape_without_a_plist_is_not_bundled() {
        var bundled = InstallProvenance.IsAppBundled("/Applications/Kurrent Capacitor.app/Contents/MacOS/kcap", _ => false);

        await Assert.That(bundled).IsFalse();
    }

    [Test]
    [Arguments("/usr/local/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap")]
    [Arguments("/Applications/Kurrent Capacitor.app/Contents/Resources/kcap")]
    [Arguments("/Applications/Kurrent Capacitor/Contents/MacOS/kcap")]
    [Arguments("")]
    public async Task Other_shapes_are_not_bundled(string path) {
        await Assert.That(InstallProvenance.IsAppBundled(path, PlistExists)).IsFalse();
    }

    [Test]
    public async Task Null_process_path_is_not_bundled() {
        await Assert.That(InstallProvenance.IsAppBundled(null, PlistExists)).IsFalse();
    }
}
