namespace Capacitor.Cli.Services;

sealed class SystemdServiceManager(UnitFileWriter? writeUnit = null) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ServiceFiles.WriteOwnerOnly;

    public string Describe() => "systemd --user unit";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(SystemdUnit.UnitPath(spec.ServiceId), SystemdUnit.Unit(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = SystemdUnit.UserUnitDir();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "kcap-daemon-*.service")
            .Select(f => SystemdUnit.IdFromUnitFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = SystemdUnit.UnitPath(serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var (_, active, _)      = ServiceProcess.Run("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        var (enabledExit, _, _) = ServiceProcess.Run("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var bin = SystemdUnit.BinaryFromUnit(File.ReadAllText(path)); // quote-aware ExecStart parse
        return new ServiceStatus(SystemdUnit.StatusFrom(active, enabledExit), bin);
    }

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking systemctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) {
        Directory.CreateDirectory(SystemdUnit.UserUnitDir());
        _writeUnit(SystemdUnit.UnitPath(spec.ServiceId), SystemdUnit.Unit(spec), null);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        WriteUnitFiles(spec);
        ServiceProcess.Check("systemctl", SystemdUnit.DaemonReloadArgs());
        ServiceProcess.Check("systemctl", SystemdUnit.EnableArgs(spec.ServiceId));
        if (startNow) ServiceProcess.Check("systemctl", SystemdUnit.RestartArgs(spec.ServiceId));
    }

    public void Uninstall(string serviceId) {
        ServiceProcess.Run("systemctl", SystemdUnit.DisableNowArgs(serviceId));
        var path = SystemdUnit.UnitPath(serviceId);
        if (File.Exists(path)) File.Delete(path);
        ServiceProcess.Run("systemctl", SystemdUnit.DaemonReloadArgs());
    }

    public void Start(string serviceId) => ServiceProcess.Check("systemctl", SystemdUnit.StartArgs(serviceId));
    public void Stop(string serviceId)  => ServiceProcess.Check("systemctl", SystemdUnit.StopArgs(serviceId));
}
