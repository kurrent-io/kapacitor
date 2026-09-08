using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Lines waiting for one <c>/hooks/transcript</c> POST. A batch closes at <see cref="MaxLines"/>
/// lines or <see cref="MaxBytes"/> of line content, whichever comes first, so a run of large lines
/// cannot grow a body past what the server binds.
/// </summary>
sealed class TranscriptBatchBuffer {
    public const int MaxLines = 100;

    /// <summary>
    /// Raw UTF-8 bytes of line content per batch. Kestrel refuses a body over 30 MB by default and
    /// JSON escaping can triple a line (non-ASCII becomes <c>\uXXXX</c>), so the raw budget stays
    /// well inside that even for a batch of pathological lines.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    readonly List<string> _lines       = [];
    readonly List<int>    _lineNumbers = [];
    int                   _bytes;

    public int  Count   => _lines.Count;
    public bool IsEmpty => _lines.Count == 0;
    public bool IsFull  => _lines.Count >= MaxLines;

    public IReadOnlyList<string> Lines       => _lines;
    public IReadOnlyList<int>    LineNumbers => _lineNumbers;

    public int FirstLineNumber => _lineNumbers[0];
    public int LastLineNumber  => _lineNumbers[^1];

    /// <summary>The bytes a line contributes to a batch; over <see cref="MaxBytes"/> it can never be posted.</summary>
    public static int SizeOf(string line) => Encoding.UTF8.GetByteCount(line);

    /// <summary>False when the line would push this batch over its byte budget, so the caller flushes first.</summary>
    public bool Fits(int bytes) => _bytes + bytes <= MaxBytes;

    public void Add(string line, int lineNumber, int bytes) {
        _lines.Add(line);
        _lineNumbers.Add(lineNumber);
        _bytes += bytes;
    }

    public void Clear() {
        _lines.Clear();
        _lineNumbers.Clear();
        _bytes = 0;
    }
}
