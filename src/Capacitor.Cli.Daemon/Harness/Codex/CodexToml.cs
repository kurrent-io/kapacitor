using System.Text;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// TOML encoding shared by every Codex launch surface that splices values into <c>-c</c> config
/// overrides — the PTY argv (<see cref="CodexLauncher"/>) and the <c>codex app-server</c> argv.
/// A single encoder keeps the <c>mcp_servers</c> / <c>hooks.state</c> overrides byte-identical
/// across transports and stops the escaping rules from drifting apart.
/// </summary>
internal static class CodexToml {
    /// <summary>Encodes <paramref name="value"/> as a TOML basic (double-quoted) string, escaping
    /// backslash, quote, the short-form control escapes, and any remaining C0 control / DEL as
    /// <c>\uXXXX</c>. The result includes the surrounding quotes.</summary>
    public static string String(string value) {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var c in value) {
            switch (c) {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\b': sb.Append("\\b");  break;
                case '\t': sb.Append("\\t");  break;
                case '\n': sb.Append("\\n");  break;
                case '\f': sb.Append("\\f");  break;
                case '\r': sb.Append("\\r");  break;
                default:
                    // Remaining C0 controls (and DEL) have no short escape — emit \uXXXX.
                    if (c < ' ' || c == (char) 0x7f) {
                        sb.Append("\\u").Append(((int) c).ToString("X4"));
                    } else {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');

        return sb.ToString();
    }
}
