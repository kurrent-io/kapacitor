using System.Text;

namespace Capacitor.App.Services;

/// The composer's bytes, in the shape the daemon's own PTY input path uses: one bracketed
/// paste so the TUI takes the text as a block, and a separate carriage return to submit it.
public static class TerminalInputEncoder {
    public static readonly byte[] Submit = "\r"u8.ToArray();

    public static byte[] Paste(string text) {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return Encoding.UTF8.GetBytes("\x1b[200~" + normalized + "\x1b[201~");
    }
}
