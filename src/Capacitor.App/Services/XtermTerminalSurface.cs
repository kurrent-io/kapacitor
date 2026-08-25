namespace Capacitor.App.Services;

using System.Text;
using SvcSystems.UI.Terminal;

/// Production ITerminalSurface wrapping SvcSystems.UI.Terminal's TerminalControlModel.
///
/// InputProduced fans in TWO distinct sources, both discovered in Task 8 and neither
/// re-exposing the other: TerminalControlModel.UserInput (keyboard/mouse-originated bytes,
/// ReadOnlyMemory&lt;byte&gt; already) and Terminal.Engine.DataReceived (terminal-originated
/// protocol replies, e.g. a DSR/CPR answer — only reachable via the raw XTerm.NET engine
/// object, which neither the SvcSystems Terminal wrapper nor the model re-expose; its payload
/// is a string that must be UTF-8 encoded before it can join the same byte[] event). Both are
/// needed for a correct PTY round trip: dropping either one silently breaks either keystrokes
/// or terminal-side query/response protocols (cursor position reports, etc.).
public sealed class XtermTerminalSurface : ITerminalSurface {
    /// The VM-owned model handle the view binds (Task 12).
    public TerminalControlModel Model { get; }

    public event Action<byte[]>? InputProduced;
    public event Action<int, int>? Resized;

    // Terminal (the SvcSystems wrapper), not the model itself — the model has no direct Cols/Rows
    // of its own (Task 8 discovery); Terminal.Cols/Rows are live, tracking every resize applied
    // via Model.Terminal.Resize(cols, rows).
    public (int Cols, int Rows) CurrentSize => (Model.Terminal.Cols, Model.Terminal.Rows);

    public XtermTerminalSurface(int cols, int rows) {
        Model = new TerminalControlModel(new TerminalOptions {
            Cols = cols,
            Rows = rows,
            ReflowOnResize = false,
        });

        Model.UserInput += OnUserInput;
        Model.SizeChanged += OnSizeChanged;
        Model.Terminal.Engine.DataReceived += OnDataReceived;
    }

    public void Feed(string text) => Model.Feed(text);

    public void EnsureCaretVisible() => Model.Terminal.Engine.CursorVisible = true;

    void OnUserInput(object? sender, TerminalUserInputEventArgs e) =>
        InputProduced?.Invoke(e.Data.ToArray());

    // Terminal-originated protocol reply (DSR/CPR etc.) — the engine hands back a decoded
    // string, not bytes; the PTY side needs bytes, so encode here rather than push the
    // encoding concern onto every consumer of InputProduced.
    void OnDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) =>
        InputProduced?.Invoke(Encoding.UTF8.GetBytes(e.Data));

    void OnSizeChanged(object? sender, TerminalSizeChangedEventArgs e) =>
        Resized?.Invoke(e.Cols, e.Rows);
}
