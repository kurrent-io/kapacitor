using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceManagerFactoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task ForPlatform_returns_each_concrete_manager() {
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd, Config.Root)).IsTypeOf<LaunchdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.Systemd, Config.Root)).IsTypeOf<SystemdServiceManager>();
        await Assert.That(ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask, Config.Root)).IsTypeOf<WindowsScheduledTaskServiceManager>();
    }

    [Test]
    public async Task ForCurrentOs_does_not_throw_on_this_host() {
        var mgr = ServiceManagerFactory.ForCurrentOs(Config.Root);
        await Assert.That(mgr.Describe()).IsNotNull();
    }

    [Test]
    public async Task Launchd_GenerateFiles_returns_one_file() {
        var spec = new ServiceSpec("laptop", "/opt/kcap/kcap-daemon", "/tmp/daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.Launchd, Config.Root).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Path).EndsWith("io.kurrent.kcap.daemon.laptop.plist");
    }

    [Test]
    public async Task Windows_GenerateFiles_returns_xml_and_wrapper() {
        var spec = new ServiceSpec("laptop", @"C:\kcap\kcap-daemon.exe", @"C:\tmp\daemon-laptop.log",
            new Dictionary<string, string>(), []);
        var files = ServiceManagerFactory.ForPlatform(ServicePlatform.WindowsScheduledTask, Config.Root).GenerateFiles(spec);
        await Assert.That(files.Count).IsEqualTo(2);
    }
}
