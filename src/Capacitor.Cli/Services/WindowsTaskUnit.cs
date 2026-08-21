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
        // The execution MODE is part of the artifact, not inherited from the machine. Delayed expansion is
        // off by default but can be turned on for every cmd session through the Command Processor registry
        // key, and `!NAME!` expands INSIDE double quotes — so a value like `!PAYLOAD!` survives quoting and
        // CmdValue (which doubles `%`, not `!`) and could expand after serialization to text containing a
        // closing quote and a command separator. The one-line `setlocal` makes the wrapper's own behaviour
        // deterministic; the Task action additionally passes /V:OFF so the mode is fixed before this file is
        // even opened. Review's point: relying on a default is not a guarantee.
        sb.Append("setlocal DisableDelayedExpansion\r\n");
        foreach (var (k, v) in spec.Environment) {
            RequireRepresentable(k, v);
            sb.Append($"set \"{ServiceText.CmdValue(k)}={ServiceText.CmdValue(v)}\"\r\n");
        }
        // Every value on the exec line is guarded and quoted, not just the binary path.
        //
        // `%`-doubling alone is not enough and quoting-when-it-contains-a-space is not enough: cmd treats
        // `& | < > ( ) ^` as live metacharacters OUTSIDE double quotes, so an argument like `foo&calc.exe`
        // contains neither a space nor a percent, renders bare, and cmd runs the tail as a second command —
        // in a file the OS executes at every logon. Inside double quotes cmd stops treating them as
        // metacharacters, so quoting unconditionally is what closes the class; `%` still expands inside
        // quotes, which is what CmdValue is for; and `"` / CR / LF cannot be represented at all, so they are
        // rejected rather than escaped.
        //
        // Unlike the binary and log paths, ExtraArgs are NOT constrained by Windows filename rules — review
        // made exactly that distinction, and it is why the "a path cannot contain a quote" reasoning that
        // covers the other two does not extend to them.
        var args = new[] { "--name", ExecValue("the service id", spec.ServiceId),
                           "--log-file", ExecValue("the log path", spec.LogPath) }
            .Concat(spec.ExtraArgs.Select(a => ExecValue("a daemon argument", a)));
        sb.Append($"{ExecValue("the daemon binary path", spec.DaemonBinaryPath)} {string.Join(' ', args)}\r\n");
        return sb.ToString();
    }

    /// <summary>Guard, escape, then quote one value interpolated into the wrapper's exec line.</summary>
    static string ExecValue(string what, string value) {
        if (IsUnrepresentable(value))
            throw new InvalidOperationException(
                $"Cannot write the service wrapper: {what} ('{value}') contains a quote or newline. Neither "
              + "can be carried safely on a batch exec line — a quote ends the quoted argument and a newline "
              + "ends the command — so the wrapper would execute something other than the daemon. Correct it, "
              + "then re-run `kcap daemon service install`.");

        return Quote(ServiceText.CmdValue(value));
    }

    /// <summary>
    /// Double-quote a value, doubling a trailing backslash run first.
    ///
    /// <para>The Windows command-line parser the daemon's own runtime uses treats <c>\"</c> as an escaped
    /// quote, so <c>"C:\dir\"</c> would swallow the closing quote and merge this argument with the next.
    /// Doubling the run — <c>"C:\dir\\"</c> — is the documented encoding for a literal trailing backslash.
    /// Reachable through an ExtraArgs value; the two paths here never end in a separator.</para>
    /// </summary>
    static string Quote(string s) {
        var trailing = s.Length - s.TrimEnd('\\').Length;

        return $"\"{s}{new string('\\', trailing)}\"";
    }

    /// <summary>
    /// Guard, then escape — every string interpolated into the Task XML goes through here, mirroring the
    /// plist writer's helper.
    ///
    /// <para>Composition does not imply XML validity: <c>wrapperPath</c> carries <c>KCAP_CONFIG_DIR</c>
    /// verbatim, Windows paths are native UTF-16, and U+FFFE, U+FFFF or a malformed surrogate unit is outside
    /// XML 1.0 while <c>SecurityElement.Escape</c> passes all of them through untouched. The consequence is
    /// the same availability failure the plist had — the task cannot be registered — so it gets the same
    /// treatment. Applied to the service id too, which is sanitized and therefore safe already: a structural
    /// invariant that holds at every interpolation is worth more than one applied where it is needed, because
    /// the next value added here inherits it.</para>
    /// </summary>
    static string Guarded(string what, string value) {
        ServiceText.RequireXmlRepresentableValue(what, value);
        return ServiceText.Xml(value);
    }

    /// <summary>
    /// Rejects a wrapper path <c>cmd /c</c> cannot be handed safely.
    ///
    /// <para>The Task action's argument text is parsed by cmd, not by CreateProcess, so it is a command line
    /// and not an opaque string. Two characters cannot be made safe there:</para>
    ///
    /// <para><c>%</c> — cmd expands <c>%NAME%</c> in the <c>/c</c> command text before opening the file, and
    /// the <c>%%</c> escape is a BATCH-FILE construct that does not apply to a command line. There is
    /// therefore no encoding for a literal percent at this sink, only refusal. (The wrapper's own body is a
    /// batch file, which is why <see cref="ServiceText.CmdValue"/> is the right answer there and not here —
    /// the same character, two sinks, two different correct treatments.)</para>
    ///
    /// <para><c>"</c>, CR, LF — structural: they end the quoted command or the line.</para>
    ///
    /// <para>Reachability, since <c>PathHelpers</c> composing the path does NOT make it safe — review made
    /// exactly that point: the path runs through the config directory, so it carries the account name, and
    /// Windows permits both <c>%</c> and <c>&amp;</c> in an account name; <c>KCAP_CONFIG_DIR</c> can set it
    /// outright. <c>&amp;</c> itself is handled by the nested-quote form below rather than refused, so an
    /// ordinary path keeps working.</para>
    /// </summary>
    static void RequireSafeWrapperPath(string wrapperPath) {
        var bad = wrapperPath.Contains('%') ? "a percent sign"
                : IsUnrepresentable(wrapperPath) ? "a quote or newline"
                : null;
        if (bad is null) return;

        throw new InvalidOperationException(
            $"Cannot register the scheduled task: the wrapper path '{wrapperPath}' contains {bad}, which "
          + "cannot be passed safely to `cmd /c` — a percent sign is expanded before the file is opened and "
          + "has no escape on a command line. Set KCAP_CONFIG_DIR to a path without it, then re-run "
          + "`kcap daemon service install`.");
    }

    /// <summary>
    /// The Task Scheduler XML. The action is <c>cmd /d /s /v:off /c ""&lt;wrapper&gt;""</c> — every switch
    /// there is load-bearing:
    ///
    /// <para><c>/s</c> with the command text both starting and ending in a quote makes cmd strip exactly the
    /// outer pair and take the remainder verbatim. Without it, cmd applies its conditional quote-stripping
    /// rules, and a path containing <c>&amp;</c> (legal in a Windows account name, so legal in this path)
    /// fails those conditions: the quotes come off and the metacharacter is parsed as command syntax. Hence
    /// the doubled quotes — the inner pair is what survives to quote the path.</para>
    ///
    /// <para><c>/v:off</c> fixes delayed expansion off before the wrapper is opened, so <c>!NAME!</c> is inert
    /// even where the machine enables it by default. <c>/d</c> skips AutoRun commands, so a per-user AutoRun
    /// value cannot inject a command ahead of the daemon.</para>
    /// </summary>
    public static string TaskXml(ServiceSpec spec, string wrapperPath) {
        RequireSafeWrapperPath(wrapperPath);

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>kcap daemon ({Guarded("the service id", spec.ServiceId)})</Description>
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
              <Arguments>/d /s /v:off /c ""{Guarded("the wrapper path", wrapperPath)}""</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

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
