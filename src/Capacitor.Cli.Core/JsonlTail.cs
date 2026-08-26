using System.Text;

namespace Capacitor.Cli.Core;

public enum TailStatus { Ok, Reset, Missing, Failed }

public sealed record TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure = null);

/// Appended-lines reader over a JSONL file another process is writing. Every open shares
/// read/write/delete: a FileShare.Read open would deny the agent its own write handle on
/// Windows. Only a length regression resets the cursor — a replacement by a same-or-longer
/// file is read from the old cursor, which both vendors' append-only transcripts never produce.
public sealed class JsonlTail(string path) {
    long _cursor;

    public string Path { get; } = path;
    public long Cursor => _cursor;

    public TailRead ReadAppended() {
        try {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var status = TailStatus.Ok;
            var length = stream.Length;
            if (length < _cursor) {
                _cursor = 0;
                status = TailStatus.Reset;
            }
            if (length == _cursor) return new TailRead([], status);

            stream.Position = _cursor;
            var buffer = new byte[length - _cursor];
            var read = 0;
            while (read < buffer.Length) {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            var lines = SplitCompleteLines(buffer.AsSpan(0, read), out var consumed);
            _cursor += consumed;
            return new TailRead(lines, status);
        } catch (FileNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (DirectoryNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (Exception ex) {
            return new TailRead([], TailStatus.Failed, ex.Message);
        }
    }

    /// Complete lines only; `consumed` stops after the last '\n' so an unterminated tail is
    /// re-read whole once its newline lands.
    public static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed) {
        var lines = new List<string>();
        consumed = 0;
        var start = 0;
        for (var i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\n') continue;
            var line = bytes[start..i];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!IsBlank(line)) lines.Add(Encoding.UTF8.GetString(line));
            start = i + 1;
            consumed = start;
        }
        return lines;
    }

    static bool IsBlank(ReadOnlySpan<byte> line) {
        foreach (var b in line) {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        }
        return true;
    }
}
