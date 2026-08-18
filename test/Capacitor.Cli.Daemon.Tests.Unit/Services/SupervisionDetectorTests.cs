using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class SupervisionDetectorTests {
    static SupervisionMode Detect(Dictionary<string, string> env, string name = "laptop",
        bool hasLogFile = true, string? cgroup = null, int pid = 4242) =>
        SupervisionDetector.Detect(env, name, hasLogFile, cgroup, pid);

    [Test]
    public async Task Marker_matching_name_is_supervised() =>
        await Assert.That(Detect(new() { ["KCAP_DAEMON_SUPERVISED"] = "laptop" }))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Marker_for_different_name_is_not_supervised() =>
        await Assert.That(Detect(new() { ["KCAP_DAEMON_SUPERVISED"] = "ci" }))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task Systemd_cgroup_plus_exec_pid_match_is_supervised() =>
        await Assert.That(Detect(new() { ["SYSTEMD_EXEC_PID"] = "4242" },
            cgroup: "0::/user.slice/user-1000.slice/.../kcap-daemon-laptop.service"))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Systemd_cgroup_with_inherited_exec_pid_is_not_supervised() =>
        await Assert.That(Detect(new() { ["SYSTEMD_EXEC_PID"] = "999" /* parent's pid */ },
            cgroup: "0::/user.slice/.../kcap-daemon-laptop.service"))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task Launchd_exact_label_is_supervised() =>
        await Assert.That(Detect(new() { ["XPC_SERVICE_NAME"] = "io.kurrent.kcap.daemon.laptop" }))
            .IsEqualTo(SupervisionMode.Supervised);

    [Test]
    public async Task Launchd_different_label_is_not_supervised() =>
        await Assert.That(Detect(new() { ["XPC_SERVICE_NAME"] = "io.kurrent.kcap.daemon.other" }))
            .IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task No_signals_with_logfile_is_detached() =>
        await Assert.That(Detect(new(), hasLogFile: true)).IsEqualTo(SupervisionMode.Detached);

    [Test]
    public async Task No_signals_no_logfile_is_foreground() =>
        await Assert.That(Detect(new(), hasLogFile: false)).IsEqualTo(SupervisionMode.Foreground);
}
