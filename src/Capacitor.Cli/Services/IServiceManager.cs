using System.Text;
namespace Capacitor.Cli.Services;

/// <summary>Which OS service backend a manager targets.</summary>
enum ServicePlatform { Launchd, Systemd, WindowsScheduledTask }

/// <summary>Lifecycle state of an installed service for one id.</summary>
enum ServiceState { NotInstalled, Installed, Running }

/// <summary>Tri-state result of probing a launchd service via launchctl print.</summary>
enum LabelProbe { Loaded, Absent, Unknown }

/// <summary>A file the manager writes at install time (absolute path + content).</summary>
record GeneratedFile(string Path, string Content);

/// <summary>Status plus the binary path baked into the installed unit (for doctor).</summary>
record ServiceStatus(ServiceState State, string? BinaryPath);

/// <summary>Rich query result: tri-state probe, plist presence, current state, binary path, running job pid.</summary>
record ServiceQuery(LabelProbe Probe, bool UnitPresent, ServiceState State, string? BinaryPath, int? JobPid);

/// <summary>
/// Everything needed to render and register one per-user service.
/// <paramref name="ServiceId"/> is the sanitized id (see <see cref="ServiceText.ServiceId"/>)
/// used for the filename/label/instance/task AND the daemon <c>--name</c>.
/// </summary>
record ServiceSpec(
    string                              ServiceId,
    string                              DaemonBinaryPath,
    string                              LogPath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>               ExtraArgs);

/// <summary>How a manager writes a unit to disk. Production is
/// <see cref="ServiceFiles.WriteOwnerOnly"/>; tests inject a spy so the wiring is assertable without
/// registering a real OS service.</summary>
delegate void UnitFileWriter(string path, string content, Encoding? encoding = null);

interface IServiceManager {
    string Describe();
    IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec);
    IReadOnlyList<string>        ListInstalled();
    ServiceStatus                Status(string serviceId);
    ServiceQuery                 Query(string serviceId);
    void Install(ServiceSpec spec, bool startNow);
    /// <summary>True + no plist on disk is the only success. False retains the plist and names the state
    /// in <paramref name="error"/> so an operator can diagnose before retrying.</summary>
    bool Uninstall(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>; no plist mutation either way.</summary>
    bool Start(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>. The plist is never deleted —
    /// stopping is not uninstalling.</summary>
    bool Stop(string serviceId, out string? error);
}
