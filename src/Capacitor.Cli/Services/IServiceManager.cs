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
    /// <summary>Write + activate only, no leading bootout — the fresh-install half of <see cref="Install"/>
    /// for the verify engine, which classifies the label itself via <see cref="Query"/> and only calls
    /// this on a positive Absent (so there is nothing to boot out). Managers with no distinct verify path
    /// delegate mechanically to <see cref="Install"/>.</summary>
    void WriteAndBootstrap(ServiceSpec spec);
    /// <summary>True + no plist on disk is the only success. False retains the plist and names the state
    /// in <paramref name="error"/> so an operator can diagnose before retrying.</summary>
    bool Uninstall(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>; no plist mutation either way.</summary>
    bool Start(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>. The plist is never deleted —
    /// stopping is not uninstalling.</summary>
    bool Stop(string serviceId, out string? error);
}

/// <summary>
/// The launchctl-invoking operations the <c>--verify</c> transaction drives, each bounded by a
/// per-call <c>timeout</c> carved from the transaction's remaining budget. A launchctl
/// child that exceeds it is tree-killed and mapped to a bounded failure (Query → <see
/// cref="LabelProbe.Unknown"/>; Start/Stop/Uninstall → false; WriteAndBootstrap → throw) so a hung
/// tool can never block the transaction past its deadline. Only <see cref="LaunchdServiceManager"/>
/// implements it — verify is launchd-only; the plain verbs keep the un-timed <see
/// cref="IServiceManager"/> methods, which stay unbounded.
/// </summary>
interface IVerifyServiceManager {
    IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec);
    ServiceQuery Query(string serviceId, TimeSpan timeout);
    void         WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout);
    bool         Uninstall(string serviceId, TimeSpan timeout, out string? error);
    bool         Start(string serviceId, TimeSpan timeout, out string? error);
    /// <summary>Bootstrap-only start for the gated path: activates the already-written unit, but
    /// fails — never kickstarts — when the label reads Loaded, closing the race with the gate's own
    /// confirmed-absent check.</summary>
    bool         StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error);
    bool         Stop(string serviceId, TimeSpan timeout, out string? error);
}
