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

    /// <summary>A decoy <c>&lt;array&gt;</c> planted before the real <c>ProgramArguments</c> must
    /// never be read as the binary — <see cref="LaunchdUnit.BinaryFromPlist"/> pairs the array with
    /// its OWN preceding <c>&lt;key&gt;ProgramArguments&lt;/key&gt;</c>, not "the document's first
    /// array". A foreign writer relying on the old first-array behavior would pass the digest gate
    /// while launchd actually executes the later, real ProgramArguments.</summary>
    [Test]
    public async Task BinaryFromPlist_ignores_a_decoy_array_before_the_real_ProgramArguments() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.decoy</string>
              <key>Decoy</key><array>
                <string>/bin/evil-daemon</string>
              </array>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
            </dict>
            </plist>
            """;
        await Assert.That(LaunchdUnit.BinaryFromPlist(xml)).IsEqualTo("/bin/kcap-daemon");
    }

    [Test]
    public async Task BinaryFromPlist_throws_on_a_duplicate_ProgramArguments_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>ProgramArguments</key><array>
                <string>/bin/evil-daemon</string>
              </array>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdUnit.BinaryFromPlist(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task EnvFromPlist_throws_on_a_duplicate_top_level_EnvironmentVariables_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
              </dict>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>allow</string>
              </dict>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdUnit.EnvFromPlist(xml)).Throws<InvalidDataException>();
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

    /// <summary>This file is never hand-edited by <see cref="LaunchdUnit.Plist"/> — a duplicate
    /// key can only mean a foreign/corrupt writer. Last-win would let a gate caller silently trust
    /// whichever value happened to land last; throwing forces every caller's existing "unreadable
    /// evidence" containment to see it instead.</summary>
    [Test]
    public async Task EnvFromPlist_throws_on_a_duplicate_key_rather_than_last_win() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>allow</string>
              </dict>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdUnit.EnvFromPlist(xml)).Throws<InvalidDataException>();
    }

    // ── EnvFromPlist: the three malformed keyed-structure shapes finding #3 of the round-3 review
    // requires to throw rather than silently degrade (empty map / skipped pair / dodged duplicate
    // detection). ──

    [Test]
    public async Task EnvFromPlist_throws_when_EnvironmentVariables_is_paired_with_a_non_dict() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><array>
                <string>not a dict at all</string>
              </array>
            </dict>
            </plist>
            """;
        // Previously silently returned an empty map (the `el.Name == "dict"` guard skipped the
        // whole block without complaint) — a non-dict pairing is ambiguous/malformed evidence, not
        // "no env vars", so it must throw.
        await Assert.That(() => LaunchdUnit.EnvFromPlist(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task EnvFromPlist_throws_when_a_key_is_paired_with_a_non_string_value() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><integer>1</integer>
              </dict>
            </dict>
            </plist>
            """;
        // Previously silently skipped the pair (the `kv.Name == "string"` guard just fell through)
        // — a relevant key paired with the wrong value type is malformed evidence, not absence.
        await Assert.That(() => LaunchdUnit.EnvFromPlist(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task EnvFromPlist_throws_on_consecutive_key_nodes_rather_than_silently_overwriting() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key>
                <key>KCAP_PROFILE</key><string>work</string>
              </dict>
            </dict>
            </plist>
            """;
        // Previously the second <key> silently overwrote the pending key, dropping
        // KCAP_CONSENT_SEED_DEFAULT entirely without complaint and dodging duplicate-key detection
        // for whatever the dropped key would otherwise have collided with.
        await Assert.That(() => LaunchdUnit.EnvFromPlist(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task StatusFromPrint_maps_exit_and_state() {
        await Assert.That(LaunchdUnit.StatusFromPrint(exitCode: 1, stdout: "")).IsEqualTo(ServiceState.NotInstalled);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = running")).IsEqualTo(ServiceState.Running);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = not running")).IsEqualTo(ServiceState.Installed);
    }
}
