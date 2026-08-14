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

    /// <summary>Distinguishes genuine absence from present-but-unreadable evidence.</summary>
    internal enum PlistRead { Absent, Ok, Unreadable }

    /// <summary>Discriminated read: a not-found blocked by structural evidence (a link at the exact
    /// path, or a file/link ancestor) is <see cref="PlistRead.Unreadable"/>, never <see cref="PlistRead.Absent"/>.</summary>
    internal static PlistRead TryReadPlist(string path, out string? content) {
        try {
            content = File.ReadAllText(path);
            return PlistRead.Ok;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            content = null;
            return PathEvidence.PathBlockedByFileOrLink(path) ? PlistRead.Unreadable : PlistRead.Absent;
        } catch {
            content = null;
            return PlistRead.Unreadable;
        }
    }

    /// <summary>Strict key/value walk: throws on any malformed key/value alternation.</summary>
    static IEnumerable<(string Key, XElement Value)> KeyedElements(XElement dict, string context) {
        string? pendingKey = null;
        foreach (var el in dict.Elements()) {
            if (el.Name == "key") {
                if (pendingKey is not null)
                    throw new InvalidDataException($"{context}consecutive <key> nodes ('{pendingKey}', '{el.Value}') in plist");
                pendingKey = el.Value;
                continue;
            }

            if (pendingKey is null)
                throw new InvalidDataException($"{context}value with no preceding key in plist");

            yield return (pendingKey, el);
            pendingKey = null;
        }

        if (pendingKey is not null)
            throw new InvalidDataException($"{context}dangling <key>{pendingKey}</key> with no value in plist");
    }

    /// <summary>The element paired with the top-level <c>&lt;key&gt;</c> named
    /// <paramref name="key"/>, or null when that key never appears; a duplicate throws.</summary>
    static XElement? TopLevelValue(XElement dict, string key) {
        XElement? result = null;
        var found = false;
        foreach (var (k, value) in KeyedElements(dict, "")) {
            if (k != key) continue;
            if (found) throw new InvalidDataException($"duplicate {key} key in plist");
            found  = true;
            result = value;
        }
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
    /// <c>EnvironmentVariables</c> key, walked strictly (see <see cref="KeyedElements"/>). Empty
    /// only when the block is genuinely absent; any other malformed shape throws
    /// <see cref="InvalidDataException"/> rather than degrading silently — this file is never
    /// hand-edited, so a gate caller reading identity evidence here must see ambiguous evidence
    /// as unreadable, never guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvFromPlist(string plistXml) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        if (topDict is null) return result;

        var envDict = TopLevelValue(topDict, "EnvironmentVariables");
        if (envDict is null) return result; // genuinely absent — a legitimate "no baked env" shape

        if (envDict.Name != "dict")
            throw new InvalidDataException("EnvironmentVariables is not a dict in plist");

        foreach (var (key, value) in KeyedElements(envDict, "EnvironmentVariables has ")) {
            if (value.Name != "string")
                throw new InvalidDataException($"EnvironmentVariables key '{key}' is paired with a non-string value in plist");
            if (!result.TryAdd(key, value.Value))
                throw new InvalidDataException($"duplicate EnvironmentVariables key '{key}' in plist");
        }

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
