using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user Windows logon Scheduled Task.</summary>
static class WindowsTaskUnit {
    const string Prefix = "kcap-daemon-";

    public static string TaskName(string id) => Prefix + id;

    public static string WrapperPath(string id) => PathHelpers.ConfigPath($"daemon-service-{id}.cmd");

    /// <summary>
    /// A value this wrapper cannot represent safely.
    ///
    /// <para><c>ServiceText.CmdValue</c> escapes <c>%</c> and nothing else, and each variable is emitted as
    /// <c>set "K=V"</c>. An embedded <c>"</c> therefore CLOSES the quoted assignment, after which
    /// <c>&amp;</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c> and <c>^</c> are live batch metacharacters in a file
    /// the service executes — arbitrary command execution in the daemon's own startup wrapper. A newline
    /// ends the <c>set</c> line outright and makes the remainder a command.</para>
    ///
    /// <para>No legitimate value reaches this: not a path (Windows filenames cannot contain <c>"</c>), not
    /// a project id, region, boolean, or URL.</para>
    /// </summary>
    static bool IsUnrepresentable(string value) =>
        value.Contains('"') || value.Contains('\r') || value.Contains('\n');

    /// <summary>
    /// Rejects a value the wrapper cannot represent, naming the key.
    ///
    /// <para><b>The check lives HERE, at the sink, and applies to every key — not at
    /// <see cref="ServiceEnvironment.Build"/>.</b> Build is not a boundary: <c>DaemonCommands</c> composes
    /// the service environment as <c>new Dictionary(Capture(profile)) { ["KCAP_DAEMON_SUPERVISED"] = id }</c>,
    /// adding an entry AFTER Capture returns, so a value already reaches this writer today without passing
    /// through Build. Checking at one of several doors is not a check.</para>
    ///
    /// <para>Applying it to every key — <c>PATH</c> and <c>KCAP_URL</c> included — closes a pre-existing
    /// exposure rather than only declining to widen it. Failing the install is the right failure: the
    /// trigger is a value that cannot be represented at all, <c>service install</c> is interactive, and the
    /// alternative is writing a file that might execute something.</para>
    /// </summary>
    static void RequireRepresentable(string key, string value) {
        // The KEY is interpolated into the same `set "K=V"` line, so it is exactly as dangerous as the
        // value — a key containing a quote closes the assignment just the same. Review caught this: the
        // first version checked only the value, while the stated rationale (callers add entries after
        // ServiceEnvironment.Build, so the sink must validate) applies to both sides equally. `=` is
        // rejected too, since it splits the assignment even without a quote.
        ServiceText.RequireValidEnvName(key);

        if (!IsUnrepresentable(value)) return;

        throw new InvalidOperationException(
            $"Cannot write the service wrapper: the value of '{key}' contains a quote or newline, which "
          + $"this platform's `set \"K=V\"` wrapper cannot carry safely — the assignment would end early "
          + $"and the remainder would be interpreted as commands. Unset or correct '{key}', then re-run "
          + $"`kcap daemon service install`.");
    }

    /// <summary>.cmd wrapper: set the captured env, then exec the daemon (no Environment element in Task XML).</summary>
    public static string Wrapper(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        foreach (var (k, v) in spec.Environment) {
            RequireRepresentable(k, v);
            sb.Append($"set \"{ServiceText.CmdValue(k)}={ServiceText.CmdValue(v)}\"\r\n");
        }
        var args = string.Join(' ',
            new[] { "--name", spec.ServiceId, "--log-file", Quote(spec.LogPath) }
                .Concat(spec.ExtraArgs.Select(QuoteIfNeeded)));
        sb.Append($"{Quote(ServiceText.CmdValue(spec.DaemonBinaryPath))} {args}\r\n");
        return sb.ToString();
    }

    static string Quote(string s) => $"\"{s}\"";
    static string QuoteIfNeeded(string s) => s.Contains(' ') ? Quote(s) : s;

    public static string TaskXml(ServiceSpec spec, string wrapperPath) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>kcap daemon ({ServiceText.Xml(spec.ServiceId)})</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
          </Triggers>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
          </Settings>
          <Actions>
            <Exec>
              <Command>cmd.exe</Command>
              <Arguments>/c "{ServiceText.Xml(wrapperPath)}"</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public static string? IdFromTaskName(string taskName) =>
        taskName.StartsWith(Prefix, StringComparison.Ordinal) ? taskName[Prefix.Length..] : null;

    /// <summary>
    /// The daemon binary the wrapper exec's — the first quoted token of its exec
    /// line. <c>daemon doctor</c> checks THIS (not the wrapper's own existence),
    /// since the wrapper can survive while the baked kcap-daemon.exe path is stale.
    /// Reverses the <c>%%</c> cmd-escaping applied at write time.
    /// </summary>
    public static string? BinaryFromWrapper(string wrapperText) {
        var line = wrapperText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('"'));
        if (line is null) return null;
        var end = line.IndexOf('"', 1);
        return end > 1 ? line[1..end].Replace("%%", "%") : null;
    }

    // ── command vectors (schtasks) ──
    public static string[] CreateArgs(string id, string xmlPath) => ["/Create", "/TN", TaskName(id), "/XML", xmlPath, "/F"];
    public static string[] DeleteArgs(string id)                 => ["/Delete", "/TN", TaskName(id), "/F"];
    public static string[] RunArgs(string id)                    => ["/Run", "/TN", TaskName(id)];
    public static string[] EndArgs(string id)                    => ["/End", "/TN", TaskName(id)];
    public static string[] QueryArgs(string id)                  => ["/Query", "/TN", TaskName(id), "/FO", "LIST"];

    public static ServiceState StatusFromQuery(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("Running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }
}
