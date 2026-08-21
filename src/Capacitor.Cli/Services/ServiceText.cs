using System.Security;
using System.Xml;

namespace Capacitor.Cli.Services;

/// <summary>
/// Pure text helpers for rendering service units. Each value interpolated into
/// a unit/wrapper is escaped for its target format so generators never emit
/// malformed markup, regardless of daemon name / path content.
/// </summary>
static class ServiceText {
    /// <summary>XML-escape for plist and Task Scheduler XML (escapes &amp; &lt; &gt; " ').</summary>
    public static string Xml(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>Escape a value for a batch <c>set "KEY=value"</c> line: percent-signs doubled.</summary>
    public static string CmdValue(string value) => value.Replace("%", "%%");

    /// <summary>
    /// Escape a systemd <c>Environment=</c>/<c>Description=</c> value: literal percent signs doubled.
    ///
    /// <para><b>Why the doubling.</b> systemd expands <i>specifiers</i> — <c>%n</c>, <c>%h</c>, <c>%i</c> and
    /// friends — in both of those directives, so a value carrying a literal <c>%</c> does not survive: a
    /// <c>PATH</c> containing <c>%n</c> silently becomes the unit name, and a percent sequence systemd does
    /// not recognise makes it refuse to load the unit at all. <c>%%</c> is systemd's own escape for one
    /// literal percent, so this is a faithful round-trip rather than a mangling.</para>
    ///
    /// <para>This is the same primitive <see cref="CmdValue"/> applies for cmd.exe, for the same reason, and
    /// it was missing here while present there — the asymmetry is what review caught.</para>
    ///
    /// <para>It no longer normalises CR/LF to spaces: <see cref="RequireNoControlCharacters"/> refuses them
    /// at the sink first, so the replacement was unreachable — and silently rewriting a caller's value was
    /// the wrong behaviour anyway.</para>
    /// </summary>
    public static string SystemdValue(string value) => value.Replace("%", "%%");

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
        // The predicate is XmlConvert's, not char.IsControl — the two are NOT equivalent, and review caught
        // both directions of the gap. char.IsControl accepts U+FFFE, U+FFFF and lone surrogates, none of
        // which XML can carry (a lone surrogate is not even a scalar value, so the encoder either throws or
        // substitutes), and it rejects U+007F–U+009F, which XML 1.0 permits. Deferring to the platform
        // predicate makes the code match the doc comment above instead of approximating it.
        //
        // Scanned by index rather than foreach so a valid surrogate PAIR can be recognised as the one
        // supplementary character it encodes; checking `char` in isolation would reject every emoji.
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length
             && XmlConvert.IsXmlSurrogatePair(value[i + 1], c)) {
                i++; // consumed the low half too
                continue;
            }

            if (XmlConvert.IsXmlChar(c)) continue;

            throw new InvalidOperationException(
                $"Refusing to write a service unit: the value of '{name}' contains U+{(int)c:X4}, which "
              + "XML 1.0 cannot represent even escaped — the resulting plist would not load. Unset or "
              + "correct that variable, then re-run `kcap daemon service install`.");
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

    /// <summary>
    /// Rejects a value carrying a C0 control character or DEL, for the systemd writer.
    ///
    /// <para>Quoting does not make a raw control byte valid unit syntax, and this writer has no encoder for
    /// one: <see cref="SystemdValue"/> handles expansion, <c>Esc</c> handles the backslash and the double
    /// quote, and neither touches U+0001, a backspace or a vertical tab — all of which a POSIX environment
    /// value, a filename or an argv string may legally contain. systemd does document C-style and hex
    /// escapes, but this code has no way to exercise a real systemd parser, so it refuses the input rather
    /// than shipping an escape whose behaviour it has only read about. That is the same call already made for
    /// the bare <c>;</c> separator, and the same reason.</para>
    ///
    /// <para>CR and LF are included, which supersedes the old newline-to-space normalisation in
    /// <see cref="SystemdValue"/>: silently rewriting a value the caller supplied is worse than declining to
    /// write it, because a service that runs with a value nobody chose is harder to diagnose than an install
    /// that failed and said which variable was at fault.</para>
    /// </summary>
    public static void RequireNoControlCharacters(string name, string value) {
        foreach (var c in value) {
            if (!char.IsControl(c) || c > '\u007F') continue;

            throw new InvalidOperationException(
                $"Refusing to write a systemd unit: the value of '{name}' contains the control character "
              + $"U+{(int)c:X4}. A unit directive has no encoding for one that this code can verify, and "
              + "quoting does not make it valid syntax — systemd would refuse to load the unit. Unset or "
              + "correct that variable, then re-run `kcap daemon service install`.");
        }
    }
}
