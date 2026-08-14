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
    /// Pairs each top-level <c>&lt;key&gt;</c> in <paramref name="dict"/> with the single element
    /// that immediately follows it (never recursing into a nested container), and returns the
    /// element paired with <paramref name="key"/>, or null when that key never appears. Requires
    /// strict key/value alternation: a <c>&lt;key&gt;</c> immediately following another
    /// <c>&lt;key&gt;</c>, a value with no preceding key, a dangling trailing <c>&lt;key&gt;</c>,
    /// or a duplicate <paramref name="key"/> all throw <see cref="InvalidDataException"/> rather
    /// than guessing — this file is never hand-edited, so any of those shapes can only mean a
    /// foreign/corrupt writer. Callers validate the returned element's own kind (array, dict, ...)
    /// themselves — this helper only resolves identity, never a value type.
    /// </summary>
    static XElement? TopLevelValue(XElement dict, string key) {
        XElement? result = null;
        var found = false;
        string? pendingKey = null;

        foreach (var el in dict.Elements()) {
            if (el.Name == "key") {
                if (pendingKey is not null)
                    throw new InvalidDataException($"consecutive <key> nodes ('{pendingKey}', '{el.Value}') in plist");
                pendingKey = el.Value;
                continue;
            }

            if (pendingKey is null)
                throw new InvalidDataException("value with no preceding key in plist");

            if (pendingKey == key) {
                if (found) throw new InvalidDataException($"duplicate {key} key in plist");
                found  = true;
                result = el;
            }

            pendingKey = null;
        }

        if (pendingKey is not null)
            throw new InvalidDataException($"dangling <key>{pendingKey}</key> with no value in plist");

        return result;
    }

    /// <summary>
    /// The daemon binary baked into a plist — the first <c>&lt;string&gt;</c> of the
    /// <c>&lt;array&gt;</c> paired with the top-level <c>&lt;key&gt;ProgramArguments&lt;/key&gt;</c>
    /// (see <see cref="TopLevelValue"/>). Used by <c>daemon doctor</c> to detect a moved binary.
    /// </summary>
    public static string? BinaryFromPlist(string plistXml) {
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        if (topDict is null) return null;

        var programArguments = TopLevelValue(topDict, "ProgramArguments");
        return programArguments?.Name == "array" ? programArguments.Elements("string").FirstOrDefault()?.Value : null;
    }

    /// <summary>
    /// The environment baked into a plist — the dict paired with the top-level
    /// <c>&lt;key&gt;EnvironmentVariables&lt;/key&gt;</c> (see <see cref="TopLevelValue"/>), then a
    /// strict walk of that dict's own key/value pairs: used by <c>daemon service status --json</c>
    /// to surface the baked profile/server/consent evidence as UX-only fields, and by the start
    /// gate to read identity/digest evidence. Returns empty only when the
    /// <c>EnvironmentVariables</c> block is genuinely absent; every other malformed shape — a
    /// non-<c>dict</c> pairing, a non-<c>string</c> value, consecutive or dangling
    /// <c>&lt;key&gt;</c> nodes, a value with no preceding key, or a duplicate key — throws
    /// <see cref="InvalidDataException"/> rather than degrading silently, because this file is
    /// never hand-edited and a gate caller reading identity evidence out of this map must see
    /// ambiguous evidence as unreadable, never guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvFromPlist(string plistXml) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        if (topDict is null) return result;

        var envDict = TopLevelValue(topDict, "EnvironmentVariables");
        if (envDict is null) return result; // genuinely absent — a legitimate "no baked env" shape

        if (envDict.Name != "dict")
            throw new InvalidDataException("EnvironmentVariables is not a dict in plist");

        string? pendingKey = null;
        foreach (var kv in envDict.Elements()) {
            if (kv.Name == "key") {
                if (pendingKey is not null)
                    throw new InvalidDataException(
                        $"EnvironmentVariables has consecutive <key> nodes ('{pendingKey}', '{kv.Value}') in plist");
                pendingKey = kv.Value;
                continue;
            }

            if (pendingKey is null)
                throw new InvalidDataException("EnvironmentVariables has a value with no preceding key in plist");

            if (kv.Name != "string")
                throw new InvalidDataException($"EnvironmentVariables key '{pendingKey}' is paired with a non-string value in plist");

            if (!result.TryAdd(pendingKey, kv.Value))
                throw new InvalidDataException($"duplicate EnvironmentVariables key '{pendingKey}' in plist");

            pendingKey = null;
        }

        if (pendingKey is not null)
            throw new InvalidDataException($"EnvironmentVariables ends on a dangling <key>{pendingKey}</key> with no value in plist");

        return result;
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

    public static LabelProbe ClassifyPrint(int exitCode, string stdout, string stderr) {
        if (exitCode == 0) return LabelProbe.Loaded;
        return stderr.Contains("Could not find service", StringComparison.OrdinalIgnoreCase)
            ? LabelProbe.Absent
            : LabelProbe.Unknown;
    }

    public static int? PidFromPrint(string stdout) {
        foreach (var line in stdout.Split('\n')) {
            var t = line.Trim();
            if (t.StartsWith("pid = ", StringComparison.Ordinal) && int.TryParse(t["pid = ".Length..].Trim(), out var pid))
                return pid;
        }
        return null;
    }
}
