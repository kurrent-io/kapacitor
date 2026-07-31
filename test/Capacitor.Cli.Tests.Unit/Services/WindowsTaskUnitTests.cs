using System.Xml.Linq;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class WindowsTaskUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, @"C:\kcap\kcap-daemon.exe", @"C:\Users\u\.config\kcap\daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

    [Test]
    public async Task TaskName_is_per_id() {
        await Assert.That(WindowsTaskUnit.TaskName("laptop")).IsEqualTo("kcap-daemon-laptop");
    }

    [Test]
    public async Task Wrapper_sets_env_and_execs_daemon() {
        var cmd = WindowsTaskUnit.Wrapper(Spec());
        await Assert.That(cmd).Contains("set \"PATH=C:\\bin\"");
        await Assert.That(cmd).Contains("set \"KCAP_PROFILE=work\"");
        // Every value on the exec line is quoted now, not only the paths: cmd treats & | < > ( ) ^ as live
        // metacharacters outside quotes, so an unquoted argument was a command-injection surface.
        await Assert.That(cmd).Contains("\"C:\\kcap\\kcap-daemon.exe\" --name \"laptop\" --log-file \"C:\\Users\\u\\.config\\kcap\\daemon-laptop.log\" \"--max-agents\" \"8\"");
    }

    [Test]
    public async Task Wrapper_doubles_percent_in_values() {
        var spec = Spec() with { Environment = new Dictionary<string, string> { ["X"] = "50%done" } };
        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("set \"X=50%%done\"");
    }

    [Test]
    public async Task BinaryFromWrapper_extracts_daemon_path_not_wrapper() {
        var bin = WindowsTaskUnit.BinaryFromWrapper(WindowsTaskUnit.Wrapper(Spec()));
        await Assert.That(bin).IsEqualTo(@"C:\kcap\kcap-daemon.exe");
    }

    [Test]
    public async Task BinaryFromWrapper_unescapes_doubled_percent() {
        var spec = Spec() with { DaemonBinaryPath = @"C:\dir%x\kcap-daemon.exe" };
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(WindowsTaskUnit.Wrapper(spec)))
            .IsEqualTo(@"C:\dir%x\kcap-daemon.exe");
    }

    [Test]
    public async Task TaskXml_is_well_formed_and_runs_cmd_wrapper() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\u\.config\kcap\daemon-service-laptop.cmd");
        XDocument.Parse(xml); // throws if malformed
        await Assert.That(xml).Contains("<Command>cmd.exe</Command>");
        await Assert.That(xml).Contains("/c");
        await Assert.That(xml).Contains("daemon-service-laptop.cmd");
        await Assert.That(xml).Contains("<LogonTrigger>");
    }
}
