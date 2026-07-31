using System.Security;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Pure text helpers for rendering service units. Each value interpolated into
/// a unit/wrapper is escaped for its target format so generators never emit
/// malformed markup, regardless of daemon name / path content.
/// </summary>
static class ServiceText {
    /// <summary>Sanitized, portable service id — reuses the daemon lock-file rule.</summary>
    public static string ServiceId(string name) => DaemonLockPaths.Sanitize(name);

    /// <summary>XML-escape for plist and Task Scheduler XML (escapes &amp; &lt; &gt; " ').</summary>
    public static string Xml(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>Escape a value for a batch <c>set "KEY=value"</c> line: percent-signs doubled.</summary>
    public static string CmdValue(string value) => value.Replace("%", "%%");

    /// <summary>Escape a systemd <c>Environment=</c>/<c>Description=</c> value: no raw newlines.</summary>
    public static string SystemdValue(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");

    /// <summary>
    /// Rejects a VALUE that XML 1.0 cannot represent, for the plist writer.
    ///
    /// <para>Entity escaping does not help here, and that is the point: <c>SecurityElement.Escape</c> turns
    /// <c>&amp;</c> and <c>&lt;</c> into entities, but U+0001 has no legal XML 1.0 representation at all —
    /// escaped or not. XML 1.0 permits only #x9, #xA, #xD and #x20 upwards. POSIX environment values, by
    /// contrast, may contain any byte except NUL, so a captured or directly composed value really can carry
    /// one.</para>
    ///
    /// <para>The consequence is availability rather than injection: the plist becomes unparseable, so
    /// <c>launchctl</c> refuses to load it and the service cannot install or restart. Failing at write time
    /// names the variable; failing at load time produces a service that silently does not exist.</para>
    /// </summary>
    public static void RequireXmlRepresentableValue(string name, string value) {
        // Written as a plain scan rather than FirstOrDefault: that returns '\0' for "no match", which is
        // itself an illegal character here, so the sentinel and a real hit are indistinguishable without a
        // second pass. Subtlety in a security-adjacent guard is not worth the brevity.
        foreach (var c in value) {
            if (c is '\t' or '\n' or '\r' || !char.IsControl(c)) continue;

            throw new InvalidOperationException(
                $"Refusing to write a service unit: the value of '{name}' contains the control character "
              + $"U+{(int)c:X4}, which XML 1.0 cannot represent even escaped — the resulting plist would "
              + "not load. Unset or correct that variable, then re-run `kcap daemon service install`.");
        }
    }

    /// <summary>
    /// Rejects an environment-variable NAME that no unit writer can carry safely. Shared by all three
    /// sinks, because every one of them interpolates the name into a line whose structure the name can
    /// break — and each format then hands the attacker something different.
    ///
    /// <para><b>systemd is the worst of the three, which is why this is central rather than per-writer.</b>
    /// A key shaped like <c>SAFE=ok\nExecStartPre=/bin/touch /tmp/pwned\n#</c> with an ordinary value
    /// renders a valid <c>Environment=SAFE=ok</c> line, then an attacker-chosen <c>ExecStartPre=</c>
    /// directive, then comments out the remainder — a command the service runs on every restart. Value
    /// escaping does not help: <c>SystemdValue</c> normalises the VALUE, and <c>EnvAssignment</c> decides
    /// quoting from the VALUE. Neither looks at the name.</para>
    ///
    /// <para>The rule is the POSIX environment-name grammar (<c>[A-Za-z_][A-Za-z0-9_]*</c>) rather than a
    /// blacklist of the characters each format happens to fear. A blacklist has to be right three times,
    /// once per format, and a fourth writer would need it extended; an allowlist of what a legitimate name
    /// can contain is right by construction. Nothing on any capture allowlist is excluded by it, so a
    /// rejection means a caller composed the dictionary directly — which is exactly the path that bypasses
    /// <see cref="ServiceEnvironment"/>.</para>
    /// </summary>
    public static void RequireValidEnvName(string name) {
        var ok = name.Length > 0
              && (char.IsAsciiLetter(name[0]) || name[0] == '_')
              && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        if (ok) return;

        throw new InvalidOperationException(
            $"Refusing to write a service unit: '{name}' is not a valid environment variable name "
          + "([A-Za-z_][A-Za-z0-9_]*). A name outside that grammar can break the unit's own line "
          + "structure — in a systemd unit it can inject an ExecStartPre= directive that runs on every "
          + "restart. No name from the capture allowlist is affected, so this one was added directly.");
    }
}
