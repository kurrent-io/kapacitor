using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed class SystemdServiceManager(UserHome home, UnitFileWriter? writeUnit = null) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));

    public string Describe() => "systemd --user unit";

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(SystemdUnit.UnitPath(home, spec.ServiceId), SystemdUnit.Unit(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = SystemdUnit.UserUnitDir(home);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "kcap-daemon-*.service")
            .Select(f => SystemdUnit.IdFromUnitFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var (_, active, _)      = ServiceProcess.Run("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        var (enabledExit, _, _) = ServiceProcess.Run("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var bin = SystemdUnit.BinaryFromUnit(File.ReadAllText(path)); // quote-aware ExecStart parse
        return new ServiceStatus(SystemdUnit.StatusFrom(active, enabledExit), bin);
    }

    public ServiceQuery Query(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        var unitPresent = File.Exists(path);
        var (_, active, _)      = ServiceProcess.Run("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        var (enabledExit, _, _) = ServiceProcess.Run("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var bin = unitPresent ? SystemdUnit.BinaryFromUnit(File.ReadAllText(path)) : null;
        var state = SystemdUnit.StatusFrom(active, enabledExit);
        var probe = state != ServiceState.NotInstalled ? LabelProbe.Loaded : LabelProbe.Absent;
        return new ServiceQuery(probe, unitPresent, state, bin, null);
    }

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking systemctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) {
        Directory.CreateDirectory(SystemdUnit.UserUnitDir(home));
        _writeUnit(SystemdUnit.UnitPath(home, spec.ServiceId), SystemdUnit.Unit(spec), null);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        WriteUnitFiles(spec);
        ServiceProcess.Check("systemctl", SystemdUnit.DaemonReloadArgs());
        ServiceProcess.Check("systemctl", SystemdUnit.EnableArgs(spec.ServiceId));
        if (startNow) ServiceProcess.Check("systemctl", SystemdUnit.RestartArgs(spec.ServiceId));
    }

    /// <summary>No distinct verify path for systemd yet — delegate mechanically to <see cref="Install"/>.</summary>
    public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, startNow: true);

    public bool Uninstall(string serviceId, out string? error) {
        ServiceProcess.Run("systemctl", SystemdUnit.DisableNowArgs(serviceId));
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (File.Exists(path)) File.Delete(path);
        ServiceProcess.Run("systemctl", SystemdUnit.DaemonReloadArgs());
        error = null;
        return true;
    }

    public bool Start(string serviceId, out string? error) {
        ServiceProcess.Check("systemctl", SystemdUnit.StartArgs(serviceId));
        error = null;
        return true;
    }

    public bool Stop(string serviceId, out string? error) {
        ServiceProcess.Check("systemctl", SystemdUnit.StopArgs(serviceId));
        error = null;
        return true;
    }
}
