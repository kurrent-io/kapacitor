using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// The Windows wrapper must reject a value it cannot represent, and it must do so ITSELF.
///
/// <para>These call <see cref="WindowsTaskUnit.Wrapper"/> directly rather than going through
/// <c>ServiceEnvironment.Build</c>. Routing through Build would make this an end-to-end pipeline test that
/// passes even if the writer were defenceless — and Build is demonstrably not the boundary:
/// <c>DaemonCommands</c> adds <c>KCAP_DAEMON_SUPERVISED</c> to the dictionary AFTER <c>Capture</c> returns,
/// so a value reaches this writer today without passing through Build at all.</para>
///
/// <para>The consequence being guarded is command execution: each variable is emitted as
/// <c>set "K=V"</c> with only <c>%</c> escaped, so an embedded quote closes the assignment and the
/// remainder of the line becomes batch commands in a file the service runs.</para>
/// </summary>
public class WindowsTaskUnitRepresentabilityTests {
    static ServiceSpec Spec(string key, string value) => new(
        ServiceId:        "test",
        DaemonBinaryPath: @"C:\kcap\kcap.exe",
        LogPath:          @"C:\kcap\log.txt",
        Environment:      new Dictionary<string, string> { [key] = value },
        ExtraArgs:        []);

    [Test]
    [Arguments("\"")]
    [Arguments("x\" & calc.exe & \"y")]
    [Arguments("a\r\nset FOO=bar")]
    [Arguments("a\nb")]
    public async Task Wrapper_rejects_a_value_it_cannot_represent(string hostile) {
        await Assert.That(() => WindowsTaskUnit.Wrapper(Spec("GOOGLE_CLOUD_PROJECT", hostile)))
            .Throws<InvalidOperationException>();
    }

    /// <summary>Applies to EVERY key, not just the newly captured ones — scoping it to Gemini's variables
    /// would leave the same exposure live for PATH, which has always gone through this writer.</summary>
    [Test]
    [Arguments("PATH")]
    [Arguments("KCAP_URL")]
    [Arguments("KCAP_DAEMON_SUPERVISED")]
    public async Task Wrapper_rejects_regardless_of_which_key_carries_it(string key) {
        await Assert.That(() => WindowsTaskUnit.Wrapper(Spec(key, "c:\\a\" & echo pwned & \"")))
            .Throws<InvalidOperationException>();
    }

    /// <summary>The KEY is interpolated into the same `set "K=V"` line, so it is exactly as dangerous as
    /// the value. The first version checked only the value — review caught that the stated rationale
    /// (callers add entries after Build, so the sink must validate) applies to both sides equally.</summary>
    [Test]
    [Arguments("BAD\"KEY")]
    [Arguments("BAD\r\nset FOO=bar")]
    [Arguments("BAD=KEY")]
    [Arguments("")]
    public async Task Wrapper_rejects_a_hostile_environment_variable_NAME(string hostileKey) {
        await Assert.That(() => WindowsTaskUnit.Wrapper(Spec(hostileKey, "harmless")))
            .Throws<InvalidOperationException>();
    }

    /// <summary>The error has to name the key, or an operator cannot act on it.</summary>
    [Test]
    public async Task Wrapper_names_the_offending_key() {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WindowsTaskUnit.Wrapper(Spec("GOOGLE_VERTEX_BASE_URL", "https://x\"")));

        await Assert.That(ex!.Message).Contains("GOOGLE_VERTEX_BASE_URL");
    }

    /// <summary>The positive control: everything legitimate still round-trips, including a path with
    /// spaces and a value containing `%` (which the existing escaper handles). Without this, the tests
    /// above would pass on a writer that rejected everything.</summary>
    [Test]
    [Arguments("my-project-123")]
    [Arguments("us-central1")]
    [Arguments("https://gemini.example/v1?x=1")]
    [Arguments(@"C:\Program Files\creds\adc.json")]
    [Arguments("100%")]
    public async Task Wrapper_accepts_legitimate_values(string ok) {
        var wrapper = WindowsTaskUnit.Wrapper(Spec("GOOGLE_CLOUD_PROJECT", ok));

        await Assert.That(wrapper).Contains("set \"GOOGLE_CLOUD_PROJECT=");
    }
}
