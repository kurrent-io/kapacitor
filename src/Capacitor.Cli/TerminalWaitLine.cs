using Spectre.Console;

namespace Capacitor.Cli;

/// <summary>
/// A spinner block pinned to the bottom of the terminal, redrawn in place while permanent lines go on
/// scrolling above it. Stoppable mid-wait, which a <see cref="AnsiConsole.Status()"/> region is not:
/// the import needs the console for its own bars, and two live renderables cannot share one.
///
/// <para>Every write goes through one lock — the frame timer and the caller are two threads drawing to
/// the same rows.</para>
/// </summary>
/// <param name="tty">False stops all in-place drawing: an escape sequence in a redirected stream is
/// noise, and a spinner nobody can see is worse than the transitions printed plainly.</param>
/// <param name="control">Where the cursor codes go. Injectable so a test can read the moves and hides
/// back, which is the half of this nothing else can observe.</param>
sealed class TerminalWaitLine(bool tty, TextWriter? control = null) : IDisposable {
    static readonly IReadOnlyList<string> Frames = Spinner.Known.Dots.Frames;

    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);

    const string ClearLine = "\u001b[2K\r";
    const string CursorUp  = "\u001b[1A";

    readonly TextWriter _control = control ?? Console.Out;

    readonly object _gate = new();

    Timer?  _timer;
    string? _text;
    string? _offer;
    int     _drawn;
    int     _frame;
    bool    _running;

    /// <summary>How many rows the block currently occupies. The one piece of state a wrong erase would
    /// corrupt, so it is readable rather than inferred.</summary>
    internal int Drawn => _drawn;

    /// <summary>Whether anything is drawn in place. False makes every member here a no-op, which is
    /// what the caller reads to decide whether a transition needs saying as a plain line instead.</summary>
    public bool Enabled => tty;

    /// <summary>Sets what the block says, starting it if it is not already running.</summary>
    /// <param name="offer">A dim second line, or null for none.</param>
    public void Show(string text, string? offer) {
        if (!tty) return;

        lock (_gate) {
            _text  = text;
            _offer = offer;

            if (!_running) {
                _running = true;
                Cursor(visible: false);
                _timer = new Timer(_ => Tick(), null, FrameInterval, FrameInterval);
            }

            Draw();
        }
    }

    /// <summary>Writes a permanent line above the block, which is erased and redrawn around it.</summary>
    public void WriteAbove(string markup) {
        lock (_gate) {
            Erase();
            AnsiConsole.MarkupLine(markup);
            Draw();
        }
    }

    /// <summary>Takes the block down and gives the cursor back. Idempotent: the wait ends more ways
    /// than it is started, and the import stops it mid-flow.</summary>
    public void Stop() {
        lock (_gate) {
            if (!_running) return;

            _running = false;
            _timer?.Dispose();
            _timer = null;

            Erase();
            Cursor(visible: true);

            _text  = null;
            _offer = null;
        }
    }

    /// <summary>Nothing beyond <see cref="Stop"/>: the block's teardown IS giving the cursor back, so a
    /// dispose that did less than stopping would leave a terminal without one.</summary>
    public void Dispose() => Stop();

    void Tick() {
        lock (_gate) {
            if (!_running) return;

            _frame = (_frame + 1) % Frames.Count;

            Draw();
        }
    }

    void Draw() {
        Erase();

        if (!_running || _text is null) return;

        var width = Width();

        AnsiConsole.Markup($"  [cyan]{Frames[_frame]}[/] {Markup.Escape(Clip(_text, width - 5))}");
        _drawn = 1;

        if (_offer is null) return;

        // CR as well as LF: a lone LF does not return to column 0 on a console with newline
        // auto-return disabled, and the offer row would then start mid-line, wrap, and cost the
        // arithmetic below a row it does not know about.
        _control.Write("\r\n");
        AnsiConsole.Markup($"    [dim]{Markup.Escape(Clip(_offer, width - 5))}[/]");
        _drawn = 2;
    }

    /// <summary>Clears the drawn rows and leaves the cursor at the start of the first one, so the next
    /// write lands where the block was.</summary>
    void Erase() {
        for (var row = _drawn; row > 0; row--) {
            _control.Write(ClearLine);

            if (row > 1) _control.Write(CursorUp);
        }

        _drawn = 0;
    }

    /// <summary>Read live rather than from <c>AnsiConsole.Profile.Width</c>, which Spectre fixes when the
    /// console is created: a terminal narrowed mid-wait would be clipped against the old width, wrap, and
    /// desync the row count. A resize can still reflow rows underneath the block — beyond a hand-rolled
    /// one — but nothing here should be the cause of it.</summary>
    static int Width() {
        try {
            var measured = Console.WindowWidth;

            // Not raised to a comfortable minimum: a genuinely narrow terminal would then be clipped
            // against columns it does not have, wrap, and cost the erase a row. Zero means a console
            // that could not be measured, not one with no columns.
            return measured > 0 ? measured : 80;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) {
            return 80;
        }
    }

    /// <summary>Truncated rather than wrapped: a line that wraps costs a row the cursor arithmetic
    /// above does not know about, and the block would then erase the wrong rows.</summary>
    static string Clip(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width)];

    void Cursor(bool visible) => _control.Write(visible ? "\u001b[?25h" : "\u001b[?25l");
}
