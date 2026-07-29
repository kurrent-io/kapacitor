using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed class WindowsScheduledTaskServiceManager : IServiceManager {
    public string Describe() => "Windows Scheduled Task";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
        var wrapperPath = WindowsTaskUnit.WrapperPath(spec.ServiceId);
        return [
            new GeneratedFile(wrapperPath, WindowsTaskUnit.Wrapper(spec)),
            new GeneratedFile(TaskXmlTempPath(spec.ServiceId), WindowsTaskUnit.TaskXml(spec, wrapperPath)),
        ];
    }

    static string TaskXmlTempPath(string id) => PathHelpers.ConfigPath($"daemon-service-{id}.task.xml");

    public IReadOnlyList<string> ListInstalled() {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", "/Query", "/FO", "LIST");
        if (code != 0) return [];
        return [.. stdout.Split('\n')
            .Where(l => l.TrimStart().StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            .Select(l => WindowsTaskUnit.IdFromTaskName(Path.GetFileName(l.Split(':', 2)[1].Trim())))
            .Where(id => id is not null).Select(id => id!).Distinct().Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", WindowsTaskUnit.QueryArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(serviceId);
        // Report the daemon binary baked inside the wrapper (not the wrapper itself)
        // so doctor catches a moved kcap-daemon.exe even when the wrapper still exists.
        var bin = File.Exists(wrapper) ? WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(wrapper)) : null;
        return new ServiceStatus(WindowsTaskUnit.StatusFromQuery(code, stdout), bin);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        var files = GenerateFiles(spec);
        foreach (var f in files) {
            Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
            // schtasks /XML wants UTF-16; the .cmd wrapper is fine as UTF-8.
            var encoding = f.Path.EndsWith(".task.xml", StringComparison.Ordinal) ? Encoding.Unicode : Encoding.UTF8;
            File.WriteAllText(f.Path, f.Content, encoding);
            ServiceFiles.RestrictToOwner(f.Path);
        }
        var xmlPath = files.First(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal)).Path;
        ServiceProcess.Check("schtasks", WindowsTaskUnit.CreateArgs(spec.ServiceId, xmlPath));
        File.Delete(xmlPath); // the task XML is only needed for registration
        if (startNow) ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("schtasks", WindowsTaskUnit.DeleteArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(serviceId);
        if (File.Exists(wrapper)) File.Delete(wrapper);
    }

    public void Start(string serviceId) => ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("schtasks", WindowsTaskUnit.EndArgs(serviceId));
}
