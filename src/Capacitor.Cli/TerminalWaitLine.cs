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
/// <param name="measure">The terminal's width, or null where it cannot be read. Injectable because the
/// wrap it guards against happens inside Spectre's writer, where no test can see it.</param>
sealed class TerminalWaitLine(bool tty, TextWriter? control = null, Func<int?>? measure = null) : IDisposable {
    static readonly IReadOnlyList<string> Frames = Spinner.Known.Dots.Frames;

    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Below this the block is not drawn at all. The prefix is four cells before a character
    /// of the wait, so a narrower terminal wraps on the prefix alone however hard the text is clipped.</summary>
    const int MinWidth = 20;

    const string ClearLine = "\u001b[2K\r";
    const string CursorUp  = "\u001b[1A";

    readonly TextWriter _control = control ?? Console.Out;

    readonly Func<int?> _measure = measure ?? Measure;

    readonly object _gate = new();

    Timer?  _timer;
    string? _text;
    string? _offer;
    int     _drawn;
    int     _frame;
    bool    _running;
    bool    _hidden;

    /// <summary>How many rows the block currently occupies. The one piece of state a wrong erase would
    /// corrupt, so it is readable rather than inferred.</summary>
    internal int Drawn => _drawn;

    /// <summary>
    /// Whether a block is on screen for a line to change in place, which the caller reads to decide
    /// whether a line has somewhere to go or has to be said outright.
    ///
    /// <para><b>The draw that happened, not a fresh guess at whether one would.</b> Re-measuring here
    /// can disagree with what is actually drawn in either direction: suppressing the caller's plain line
    /// while no block exists, or printing one beside a block that does. The next draw is what recovers a
    /// terminal that has widened, and the frame timer means that is never more than a frame away.</para>
    /// </summary>
    public bool Pinned {
        get { lock (_gate) return _drawn > 0; }
    }

    /// <summary>Sets what the block says, starting it if it is not already running.</summary>
    /// <param name="offer">A dim second line, or null for none.</param>
    public void Show(string text, string? offer) {
        if (!tty) return;

        lock (_gate) {
            _text  = text;
            _offer = offer;

            if (!_running) {
                _running = true;
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
            ShowCursor();

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

        if (!_running || _text is null) { ShowCursor(); return; }

        // One sample, used for the whole draw: the terminal can resize between two reads, so a second
        // one can be narrower than the width just validated - or unreadable, where taking `.Value` off
        // it would throw out of a timer callback. Below MinWidth, or unreadable, nothing is drawn rather
        // than drawn wrong; the caller says its lines outright instead, which it reads off `Pinned`.
        if (_measure() is not { } width || width < MinWidth) { ShowCursor(); return; }

        HideCursor();

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
    /// desync the row count. Null where it cannot be read — there is no safe number to guess, since one
    /// wider than the terminal wraps and one narrower is the same lie the caller is being spared.</summary>
    static int? Measure() {
        try {
            return Console.WindowWidth is var w and > 0 ? w : null;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) {
            return null;
        }
    }

    /// <summary>Truncated rather than wrapped: a line that wraps costs a row the cursor arithmetic
    /// above does not know about, and the block would then erase the wrong rows.</summary>
    static string Clip(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width)];

    // Both idempotent: the block is torn down more ways than it is set up, and a show with no matching
    // hide is a stray escape in the middle of output somebody else owns.
    void HideCursor() {
        if (_hidden) return;

        _hidden = true;
        _control.Write("\u001b[?25l");
    }

    void ShowCursor() {
        if (!_hidden) return;

        _hidden = false;
        _control.Write("\u001b[?25h");
    }
}
