using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

sealed partial class LaunchdServiceManager : IServiceManager {
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static int Uid() => (int)getuid();

    public string Describe() => "launchd LaunchAgent";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(LaunchdUnit.PlistPath(spec.ServiceId), LaunchdUnit.Plist(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = LaunchdUnit.AgentsDir();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "io.kurrent.kcap.daemon.*.plist")
            .Select(f => LaunchdUnit.IdFromPlistFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = LaunchdUnit.PlistPath(serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var bin = LaunchdUnit.BinaryFromPlist(File.ReadAllText(path)); // ProgramArguments[0], not the Label
        var (code, stdout, _) = ServiceProcess.Run("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        return new ServiceStatus(LaunchdUnit.StatusFromPrint(code, stdout), bin);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        var plistPath = LaunchdUnit.PlistPath(spec.ServiceId);
        // idempotent: bootout an existing job (ignore failure), then rewrite + bootstrap.
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), spec.ServiceId));
        File.WriteAllText(plistPath, LaunchdUnit.Plist(spec));
        ServiceFiles.RestrictToOwner(plistPath);
        ServiceProcess.Check("launchctl", LaunchdUnit.BootstrapArgs(Uid(), plistPath)); // RunAtLoad starts it
        if (!startNow) ServiceProcess.Run("launchctl", LaunchdUnit.KillArgs(Uid(), spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), serviceId));
        var path = LaunchdUnit.PlistPath(serviceId);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Start(string serviceId) => ServiceProcess.Check("launchctl", LaunchdUnit.KickstartArgs(Uid(), serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("launchctl", LaunchdUnit.KillArgs(Uid(), serviceId));
}
