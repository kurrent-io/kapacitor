using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Every unit writer must reject an environment-variable NAME it cannot carry, and each one is tested
/// directly at its own sink.
///
/// <para>The name is interpolated into a line whose structure it can break, and each format then hands the
/// attacker something different. <b>systemd is the worst:</b> a key shaped like
/// <c>SAFE=ok\nExecStartPre=/bin/touch /tmp/pwned\n#</c> with an ordinary value renders a valid
/// <c>Environment=SAFE=ok</c> line, then an attacker-chosen <c>ExecStartPre=</c> that the service runs on
/// every restart, then comments out the rest. Value escaping cannot help: <c>SystemdValue</c> normalises
/// the value and <c>EnvAssignment</c> decides quoting from the value — neither looks at the name.</para>
///
/// <para>Called per writer rather than once through a shared helper: the point of a sink check is that
/// every sink has it, and a single helper test would pass while a writer forgot to call it.</para>
/// </summary>
public class UnitWriterEnvNameTests {
    static ServiceSpec Spec(string key) => new(
        ServiceId:        "test",
        DaemonBinaryPath: "/usr/local/bin/kcap",
        LogPath:          "/tmp/kcap.log",
        Environment:      new Dictionary<string, string> { [key] = "harmless" },
        ExtraArgs:        []);

    /// <summary>The systemd unit-file injection primitive, and three simpler shapes.</summary>
    [Test]
    [Arguments("SAFE=ok\nExecStartPre=/bin/touch /tmp/pwned\n#")]
    [Arguments("BAD=KEY")]
    [Arguments("BAD\"KEY")]
    [Arguments("BAD KEY")]
    [Arguments("9LEADING_DIGIT")]
    [Arguments("")]
    public async Task Systemd_rejects_a_hostile_environment_name(string key) {
        await Assert.That(() => SystemdUnit.Unit(Spec(key))).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("SAFE=ok\nExecStartPre=/bin/touch /tmp/pwned\n#")]
    [Arguments("BAD=KEY")]
    [Arguments("\u0001CTRL")]
    [Arguments("")]
    public async Task Launchd_rejects_a_hostile_environment_name(string key) {
        await Assert.That(() => LaunchdUnit.Plist(Spec(key))).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("SAFE=ok\r\nExecStartPre=/bin/touch C:\\pwned\r\n#")]
    [Arguments("BAD=KEY")]
    [Arguments("BAD\"KEY")]
    [Arguments("")]
    public async Task Windows_rejects_a_hostile_environment_name(string key) {
        await Assert.That(() => WindowsTaskUnit.Wrapper(Spec(key))).Throws<InvalidOperationException>();
    }

    /// <summary>
    /// A launchd VALUE can also be XML-unrepresentable, and escaping does not fix it: U+0001 has no legal
    /// XML 1.0 form, escaped or not, while POSIX permits any byte but NUL in a value. The failure is
    /// availability rather than injection — the plist will not load, so the service silently does not
    /// exist. Failing at write time names the variable instead.
    /// </summary>
    [Test]
    [Arguments("\u0001")]
    [Arguments("ok\u0000bad")]
    [Arguments("pre\u001Fpost")]
    public async Task Launchd_rejects_a_value_xml_cannot_represent(string hostileValue) {
        var spec = new ServiceSpec(
            ServiceId:        "test",
            DaemonBinaryPath: "/usr/local/bin/kcap",
            LogPath:          "/tmp/kcap.log",
            Environment:      new Dictionary<string, string> { ["GOOGLE_CLOUD_PROJECT"] = hostileValue },
            ExtraArgs:        []);

        await Assert.That(() => LaunchdUnit.Plist(spec)).Throws<InvalidOperationException>();
    }

    /// <summary>Tab, LF and CR ARE legal XML 1.0 and must not be rejected — the check is about the
    /// characters XML cannot carry, not about tidiness.</summary>
    [Test]
    [Arguments("a\tb")]
    [Arguments("a\nb")]
    public async Task Launchd_accepts_the_control_characters_xml_permits(string ok) {
        var spec = new ServiceSpec(
            ServiceId:        "test",
            DaemonBinaryPath: "/usr/local/bin/kcap",
            LogPath:          "/tmp/kcap.log",
            Environment:      new Dictionary<string, string> { ["GOOGLE_CLOUD_PROJECT"] = ok },
            ExtraArgs:        []);

        await Assert.That(LaunchdUnit.Plist(spec)).Contains("GOOGLE_CLOUD_PROJECT");
    }

    /// <summary>The positive control, at every sink. Without it the suites above would pass on a writer
    /// that rejected everything — including the names kcap actually captures.</summary>
    [Test]
    [Arguments("PATH")]
    [Arguments("KCAP_PROFILE")]
    [Arguments("GOOGLE_CLOUD_PROJECT")]
    [Arguments("GOOGLE_GENAI_USE_VERTEXAI")]
    [Arguments("_LEADING_UNDERSCORE")]
    public async Task Every_writer_accepts_a_legitimate_environment_name(string key) {
        await Assert.That(SystemdUnit.Unit(Spec(key))).Contains(key);
        await Assert.That(LaunchdUnit.Plist(Spec(key))).Contains(key);
        await Assert.That(WindowsTaskUnit.Wrapper(Spec(key))).Contains(key);
    }
}
