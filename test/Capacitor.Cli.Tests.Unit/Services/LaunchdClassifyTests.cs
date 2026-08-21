using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class LaunchdClassifyTests {
    [Test]
    public async Task Zero_exit_running_is_loaded() {
        await Assert.That(LaunchdUnit.ClassifyPrint(0, "state = running\npid = 924\n", "")).IsEqualTo(LabelProbe.Loaded);
    }

    [Test]
    public async Task Could_not_find_is_absent() {
        await Assert.That(LaunchdUnit.ClassifyPrint(113, "", "Could not find service \"io.kurrent.kcap.daemon.x\" in domain for user gui: 501"))
            .IsEqualTo(LabelProbe.Absent);
    }

    [Test]
    public async Task Nonzero_without_not_found_signature_is_unknown() {
        await Assert.That(LaunchdUnit.ClassifyPrint(1, "", "Operation not permitted")).IsEqualTo(LabelProbe.Unknown);
    }

    [Test]
    public async Task Pid_parsed_from_print() {
        await Assert.That(LaunchdUnit.PidFromPrint("\tstate = running\n\tpid = 924\n")).IsEqualTo(924);
    }

    [Test]
    public async Task Pid_null_when_absent() {
        await Assert.That(LaunchdUnit.PidFromPrint("\tstate = waiting\n")).IsNull();
    }
}
