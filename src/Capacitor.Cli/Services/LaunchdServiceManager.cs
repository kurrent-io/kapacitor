using System.Runtime.InteropServices;

namespace Capacitor.Cli.Services;

sealed partial class LaunchdServiceManager(
    UnitFileWriter? writeUnit = null,
    Func<string, string[], (int ExitCode, string StdOut, string StdErr)>? runProcess = null
) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _runProcess = runProcess ?? ServiceProcess.Run;

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking launchctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir());
        _writeUnit(LaunchdUnit.PlistPath(spec.ServiceId), LaunchdUnit.Plist(spec), null);
    }

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
        var (code, stdout, _) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        return new ServiceStatus(LaunchdUnit.StatusFromPrint(code, stdout), bin);
    }

    public ServiceQuery Query(string serviceId) {
        var path        = LaunchdUnit.PlistPath(serviceId);
        var unitPresent = File.Exists(path);
        var bin         = unitPresent ? LaunchdUnit.BinaryFromPlist(File.ReadAllText(path)) : null;
        var (code, stdout, stderr) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = LaunchdUnit.ClassifyPrint(code, stdout, stderr);
        var state = probe == LabelProbe.Loaded ? LaunchdUnit.StatusFromPrint(code, stdout) : ServiceState.NotInstalled;
        return new ServiceQuery(probe, unitPresent, state, bin, probe == LabelProbe.Loaded ? LaunchdUnit.PidFromPrint(stdout) : null);
    }

    public void Install(ServiceSpec spec, bool startNow) {
        var plistPath = LaunchdUnit.PlistPath(spec.ServiceId);
        // idempotent: bootout an existing job (ignore failure), then rewrite + bootstrap.
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), spec.ServiceId));
        WriteUnitFiles(spec);
        ServiceProcess.Check("launchctl", LaunchdUnit.BootstrapArgs(Uid(), plistPath)); // RunAtLoad starts it
        if (!startNow) ServiceProcess.Run("launchctl", LaunchdUnit.KillArgs(Uid(), spec.ServiceId));
    }

    /// <summary>
    /// A non-zero <c>bootout</c> is not automatically a failure: the label may simply already be unloaded
    /// (a prior uninstall, a crash-then-bootout race, launchd having reaped it itself). Re-query with
    /// <c>launchctl print</c> to tell that benign case apart from a bootout that actually failed to unload
    /// a live job — only <see cref="LabelProbe.Absent"/> is treated as success; <see cref="LabelProbe.Loaded"/>
    /// or <see cref="LabelProbe.Unknown"/> retain the plist so the operator can retry against a known state.
    /// </summary>
    public bool Uninstall(string serviceId, out string? error) {
        var path = LaunchdUnit.PlistPath(serviceId);
        var (bootoutExit, _, _) = _runProcess("launchctl", LaunchdUnit.BootoutArgs(Uid(), serviceId));

        if (bootoutExit != 0) {
            var (queryExit, stdout, stderr) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
            var probe = LaunchdUnit.ClassifyPrint(queryExit, stdout, stderr);

            if (probe != LabelProbe.Absent) {
                error = $"launchctl bootout failed (exit {bootoutExit}) and the label is still {probe} — plist retained: {path}";
                return false;
            }
        }

        if (File.Exists(path)) File.Delete(path);
        error = null;
        return true;
    }

    /// <summary>
    /// A LaunchAgent that is between short-lived <c>KeepAlive</c> incarnations can lose its job (and thus
    /// the ability to catch a SIGTERM) at any moment, so <c>kickstart</c>/<c>kill</c> raced that gap.
    /// Probing first tells "not currently loaded" from "loaded" apart, so we issue the launchctl verb that
    /// actually applies to the state on the wire: <c>bootstrap</c> to load an unloaded label,
    /// <c>kickstart</c> to restart a loaded one. An <see cref="LabelProbe.Unknown"/> probe means the print
    /// failed for a reason other than absence, so neither verb is safe to guess — fail without mutating.
    /// </summary>
    public bool Start(string serviceId, out string? error) {
        var (probeExit, probeOut, probeErr) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = LaunchdUnit.ClassifyPrint(probeExit, probeOut, probeErr);

        switch (probe) {
            case LabelProbe.Absent:
                var (bootstrapExit, _, bootstrapErr) = _runProcess("launchctl", LaunchdUnit.BootstrapArgs(Uid(), LaunchdUnit.PlistPath(serviceId)));
                if (bootstrapExit != 0) {
                    error = $"launchctl bootstrap failed (exit {bootstrapExit}): {bootstrapErr.Trim()}";
                    return false;
                }
                break;
            case LabelProbe.Loaded:
                var (kickstartExit, _, kickstartErr) = _runProcess("launchctl", LaunchdUnit.KickstartArgs(Uid(), serviceId));
                if (kickstartExit != 0) {
                    error = $"launchctl kickstart failed (exit {kickstartExit}): {kickstartErr.Trim()}";
                    return false;
                }
                break;
            default:
                error = $"cannot start '{serviceId}': launchctl print left the label state {probe} — no action taken";
                return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// See <see cref="Uninstall"/> for the same benign-absence re-query rule: a non-zero <c>bootout</c>
    /// is only a real failure if the label is still <see cref="LabelProbe.Loaded"/> or
    /// <see cref="LabelProbe.Unknown"/> afterward. Unlike <see cref="Uninstall"/>, the plist is never
    /// deleted here — stopping unloads the label, it does not remove the service.
    /// </summary>
    public bool Stop(string serviceId, out string? error) {
        var (bootoutExit, _, _) = _runProcess("launchctl", LaunchdUnit.BootoutArgs(Uid(), serviceId));

        if (bootoutExit != 0) {
            var (queryExit, stdout, stderr) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
            var probe = LaunchdUnit.ClassifyPrint(queryExit, stdout, stderr);

            if (probe != LabelProbe.Absent) {
                error = $"launchctl bootout failed (exit {bootoutExit}) and the label is still {probe}";
                return false;
            }
        }

        error = null;
        return true;
    }
}
