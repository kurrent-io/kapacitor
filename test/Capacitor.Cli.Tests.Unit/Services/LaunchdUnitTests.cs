using System.Xml.Linq;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class LaunchdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        ServiceId: id,
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath: "/home/u/.config/kcap/daemon-laptop.log",
        Environment: new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["KCAP_PROFILE"] = "work" },
        ExtraArgs: ["--max-agents", "8"]);

    [Test]
    public async Task Label_is_reverse_dns_with_id() {
        await Assert.That(LaunchdUnit.Label("laptop")).IsEqualTo("io.kurrent.kcap.daemon.laptop");
    }

    [Test]
    public async Task Plist_is_well_formed_xml_and_carries_args_and_env() {
        var plist = LaunchdUnit.Plist(Spec());
        var doc   = XDocument.Parse(plist); // throws if malformed
        await Assert.That(doc).IsNotNull();
        await Assert.That(plist).Contains("<string>/opt/kcap/kcap-daemon</string>");
        await Assert.That(plist).Contains("<string>--name</string>");
        await Assert.That(plist).Contains("<string>laptop</string>");
        await Assert.That(plist).Contains("<string>--max-agents</string>");
        await Assert.That(plist).Contains("<key>PATH</key>");
        await Assert.That(plist).Contains("<key>KCAP_PROFILE</key>");
        await Assert.That(plist).Contains("<key>SuccessfulExit</key>");
    }

    [Test]
    public async Task Plist_escapes_metacharacters_in_values() {
        var spec  = Spec() with { DaemonBinaryPath = "/opt/a&b/kcap-daemon" };
        var plist = LaunchdUnit.Plist(spec);
        XDocument.Parse(plist); // must still parse
        await Assert.That(plist).Contains("/opt/a&amp;b/kcap-daemon");
    }

    [Test]
    public async Task IdFromPlistFileName_extracts_the_id() {
        await Assert.That(LaunchdUnit.IdFromPlistFileName("io.kurrent.kcap.daemon.laptop.plist"))
            .IsEqualTo("laptop");
        await Assert.That(LaunchdUnit.IdFromPlistFileName("unrelated.plist")).IsNull();
    }

    [Test]
    public async Task BinaryFromPlist_returns_program_argument_zero_not_the_label() {
        var plist = LaunchdUnit.Plist(Spec());
        // Regression: the Label is the first <string> in the document; the binary
        // is the first <string> inside <array> (ProgramArguments).
        await Assert.That(LaunchdUnit.BinaryFromPlist(plist)).IsEqualTo("/opt/kcap/kcap-daemon");
    }

    [Test]
    public async Task EnvFromPlist_round_trips_what_Plist_writes() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> {
                ["KCAP_PROFILE"]              = "acme",
                ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
                ["KCAP_EXPECT_SERVER_URL"]    = "https://s",
            },
        };
        var xml = LaunchdUnit.Plist(spec);
        var env = LaunchdUnit.EnvFromPlist(xml);

        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("acme");
        await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("prompt");
        await Assert.That(env["KCAP_EXPECT_SERVER_URL"]).IsEqualTo("https://s");
    }

    [Test]
    public async Task EnvFromPlist_on_plist_without_env_dict_returns_empty() {
        var xml = LaunchdUnit.Plist(Spec() with { Environment = new Dictionary<string, string>() });
        await Assert.That(LaunchdUnit.EnvFromPlist(xml)).IsEmpty();
    }

    [Test]
    public async Task StatusFromPrint_maps_exit_and_state() {
        await Assert.That(LaunchdUnit.StatusFromPrint(exitCode: 1, stdout: "")).IsEqualTo(ServiceState.NotInstalled);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = running")).IsEqualTo(ServiceState.Running);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = not running")).IsEqualTo(ServiceState.Installed);
    }
}
