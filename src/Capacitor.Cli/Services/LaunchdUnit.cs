using System.Text;
using System.Xml.Linq;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user launchd LaunchAgent.</summary>
static class LaunchdUnit {
    const string LabelPrefix = "io.kurrent.kcap.daemon.";

    public static string Label(string id) => LabelPrefix + id;

    /// <summary>~/Library/LaunchAgents directory for the current user.</summary>
    public static string AgentsDir() =>
        Path.Combine(PathHelpers.HomeDirectory, "Library", "LaunchAgents");

    public static string PlistPath(string id) =>
        Path.Combine(AgentsDir(), Label(id) + ".plist");

    /// <summary>Separate stdout/stderr capture file (keeps the rolling --log-file uncluttered).</summary>
    static string OutLogPath(ServiceSpec spec) =>
        Path.ChangeExtension(spec.LogPath, null) + ".out.log";

    public static string Plist(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        sb.Append($"  <key>Label</key><string>{Guarded("the service label", Label(spec.ServiceId))}</string>\n");

        sb.Append("  <key>ProgramArguments</key><array>\n");
        foreach (var arg in ProgramArguments(spec))
            sb.Append($"    <string>{Guarded("the daemon command line", arg)}</string>\n");
        sb.Append("  </array>\n");

        sb.Append("  <key>EnvironmentVariables</key><dict>\n");
        foreach (var (k, v) in spec.Environment) {
            // XML escaping already contains the name structurally, but a control character is not legal
            // XML 1.0 at all — so the plist would be silently unparseable rather than injected. Same
            // check, same reason: one grammar, applied at every sink.
            ServiceText.RequireValidEnvName(k);
            ServiceText.RequireXmlRepresentableValue(k, v);
            sb.Append($"    <key>{ServiceText.Xml(k)}</key><string>{ServiceText.Xml(v)}</string>\n");
        }
        sb.Append("  </dict>\n");

        sb.Append("  <key>RunAtLoad</key><true/>\n");
        sb.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        sb.Append("  <key>ProcessType</key><string>Adaptive</string>\n");
        var outLog = Guarded("the daemon log path", OutLogPath(spec));
        sb.Append($"  <key>StandardOutPath</key><string>{outLog}</string>\n");
        sb.Append($"  <key>StandardErrorPath</key><string>{outLog}</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Guard, then escape — every string this writer interpolates goes through here.
    ///
    /// <para><b>Why not just escape.</b> <c>SecurityElement.Escape</c> handles the five markup characters and
    /// nothing else; it passes a C0 control, U+FFFE or a lone surrogate straight through, and none of those
    /// has any legal XML 1.0 representation. The result is a plist <c>launchctl</c> silently will not load.</para>
    ///
    /// <para><b>Why it covers the paths and not only the environment.</b> The environment loop was guarded and
    /// these four sites were not, yet <c>ProgramArguments</c> carries the binary path, the log path and the
    /// extra args, and macOS filenames may contain any byte but <c>/</c> and NUL. Guarding the environment
    /// only is the same asymmetry review already found twice at the other two sinks: applying a check to some
    /// of the values interpolated into a file is not checking the file.</para>
    /// </summary>
    static string Guarded(string what, string value) {
        ServiceText.RequireXmlRepresentableValue(what, value);
        return ServiceText.Xml(value);
    }

    /// <summary>argv the agent runs: binary, pinned --name + --log-file, then extra args.</summary>
    public static IReadOnlyList<string> ProgramArguments(ServiceSpec spec) =>
        [spec.DaemonBinaryPath, "--name", spec.ServiceId, "--log-file", spec.LogPath, .. spec.ExtraArgs];

    public static string? IdFromPlistFileName(string fileName) {
        var name = Path.GetFileNameWithoutExtension(fileName); // strips .plist
        return name.StartsWith(LabelPrefix, StringComparison.Ordinal)
            ? name[LabelPrefix.Length..]
            : null;
    }

    /// <summary>
    /// The daemon binary baked into a plist — <c>ProgramArguments[0]</c>, i.e. the
    /// first <c>&lt;string&gt;</c> inside the (sole) <c>&lt;array&gt;</c>. NOT the
    /// document's first <c>&lt;string&gt;</c>, which is the <c>Label</c>. Used by
    /// <c>daemon doctor</c> to detect a moved binary.
    /// </summary>
    public static string? BinaryFromPlist(string plistXml) {
        var array = XDocument.Parse(plistXml).Descendants("array").FirstOrDefault();
        return array?.Elements("string").FirstOrDefault()?.Value;
    }

    // ── command vectors (uid passed in so these stay pure) ──
    public static string[] BootstrapArgs(int uid, string plistPath) => ["bootstrap", $"gui/{uid}", plistPath];
    public static string[] BootoutArgs(int uid, string id)          => ["bootout", $"gui/{uid}/{Label(id)}"];
    public static string[] KickstartArgs(int uid, string id)        => ["kickstart", $"gui/{uid}/{Label(id)}"];
    public static string[] KillArgs(int uid, string id)             => ["kill", "SIGTERM", $"gui/{uid}/{Label(id)}"];
    public static string[] PrintArgs(int uid, string id)            => ["print", $"gui/{uid}/{Label(id)}"];

    public static ServiceState StatusFromPrint(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("state = running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }
}
