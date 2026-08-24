using Capacitor.App.Services;
using SvcSystems.UI.Terminal;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

// API DISCOVERY (Task 8) — decompiled via ilspycmd against the NuGet cache copies of
// SvcSystems.UI.Terminal 1.1.1 (net10.0 lib) and XTerm.NET 1.0.16 (net6.0 lib), cross-checked
// against both packages' README.md. Recorded verbatim for Tasks 10–12 to build on; do not
// rely on the README alone, it undersells a few things confirmed only in IL (marked below).
//
// ── SvcSystems.UI.Terminal.TerminalControlModel : Avalonia.AvaloniaObject ──────────────────
//   ctor TerminalControlModel(TerminalOptions? options = null)
//   Feed(string text)                          — the encode/decode-safe entrypoint. Internally
//                                                 does Encoding.UTF8.GetBytes(text) then calls
//                                                 the byte[] overload below (round-trips through
//                                                 UTF-8 once more; lossless for already-decoded
//                                                 text, just wasted work).
//   Feed(byte[] text, int length = -1)         — ⚠ NOT incremental: SvcSystems.UI.Terminal.
//                                                 Terminal.Feed(byte[],int) does a FRESH
//                                                 Encoding.UTF8.GetString(data, 0, len) on every
//                                                 call. A multibyte code point split across two
//                                                 Feed(byte[]) calls corrupts silently (each half
//                                                 becomes U+FFFD independently). This is exactly
//                                                 why Utf8StreamDecoder exists — decode PTY bytes
//                                                 externally with ONE Decoder spanning the whole
//                                                 attach attempt, then call Feed(string).
//   Send(string text) / Send(byte[] data)      — programmatic input; raises UserInput directly
//                                                 (after EnsureCaretIsVisible()).
//   event EventHandler<TerminalUserInputEventArgs> UserInput
//                                               — the keyboard/programmatic-input → bytes path.
//                                                 TerminalUserInputEventArgs.Data is a
//                                                 ReadOnlyMemory<byte> (ctor takes it directly,
//                                                 no encoding step — Send(string) UTF8-encodes
//                                                 before raising). This is what a consumer wires
//                                                 to a PTY's stdin.
//   event EventHandler<TerminalSizeChangedEventArgs> SizeChanged
//                                               — raised from Resize(...) below; args carry
//                                                 Cols, Rows, Width, Height (the last two are the
//                                                 pixel dimensions passed in, not derived).
//   Resize(double width, double height, double textWidth, double textHeight)
//                                               — pixel-based; model computes cols/rows itself
//                                                 (Math.Max(width/textWidth, 1) etc.) and calls
//                                                 Terminal.Resize(cols, rows) internally. There is
//                                                 NO Resize(int cols, int rows) on the model —
//                                                 for a direct cols/rows resize call
//                                                 model.Terminal.Resize(cols, rows) instead.
//   Terminal Terminal { get; }                 — the SvcSystems wrapper (see below); Avalonia
//                                                 DirectProperty-backed.
//   SearchService SearchService { get; }
//   Title, SelectedText, HasSelection, LastSearchText, SearchResultCount,
//   CurrentSearchResultIndex                    — Avalonia DirectProperty-backed.
//   ScrollOffset, MaxScrollback, ScrollPosition, ScrollThumbsize, CanScroll,
//   CaretColumn, CaretRow, IsCaretVisible, IsMouseModeActive, OptionAsMetaKey
//   ScrollLines(int) / PageUp() / PageDown() / ScrollToYDisp(int) / ScrollToPosition(double) /
//   EnsureCaretIsVisible()
//   StartSelection(row, col) / StartSelectionFromSoftStart() / SetSoftSelectionStart(row, col) /
//   DragExtendSelection(row, col) / ShiftExtendSelection(row, col) /
//   SelectWordOrExpression(row, col) / SelectRow(row) / SelectAll() / ClearSelection()
//   Search(string) / SelectNextSearchResult() / SelectPreviousSearchResult()
//
// ── SvcSystems.UI.Terminal.TerminalOptions (sealed class) ──────────────────────────────────
//   Cols = 80, Rows = 24, Scrollback = 1000, TabStopWidth = 8, TermName = "xterm",
//   ConvertEol = false, ReflowOnResize = true   — all settable init-style properties with the
//                                                 defaults shown; ReflowOnResize=false is what
//                                                 upstream's own sample shell sets to avoid
//                                                 corrupting full-screen TUIs (e.g. `mc`) on
//                                                 resize (README, "Sample shell notes").
//
// ── SvcSystems.UI.Terminal.Terminal (sealed wrapper class; model.Terminal) ─────────────────
//   ctor Terminal(TerminalOptions? options = null) — clamps Cols>=2, Rows>=1, Scrollback>=0,
//                                                 TabStopWidth>=1 before constructing the engine.
//   Feed(string text)                           — engine.Write(text) directly, no re-decode.
//   Feed(byte[] data, int len = -1)             — ⚠ see the model's Feed(byte[]) note above;
//                                                 same fresh-GetString-per-call behavior lives
//                                                 here (the model's byte[] overload just forwards
//                                                 to this one).
//   Resize(int cols, int rows)                  — clamps to >=1 each; use this for a direct
//                                                 cols/rows resize (not on the model).
//   SwitchToAltBuffer() / SwitchToNormalBuffer()
//   event EventHandler<TitleChangedEventArgs> TitleChanged
//   Engine  -> XTerm.Terminal                   — the underlying XTerm.NET engine object; this
//                                                 is where DataReceived and every other XTerm.NET
//                                                 event lives (NOT re-exposed by this wrapper or
//                                                 by TerminalControlModel).
//   Buffer  -> XTerm.Buffer.TerminalBuffer       — same instance as Engine.Buffer.
//   Selection -> XTerm.Selection.SelectionManager
//   IsAlternateBufferActive : bool
//   Options -> TerminalOptions (a fresh snapshot object, not the one passed to the ctor)
//   Cols, Rows, Title
//
// ── SvcSystems.UI.Terminal.TerminalUserInputEventArgs(ReadOnlyMemory<byte> data) : EventArgs
//   Data : ReadOnlyMemory<byte>
// ── SvcSystems.UI.Terminal.TerminalSizeChangedEventArgs(int cols, int rows, double width,
//    double height) : EventArgs  — Cols, Rows, Width, Height
//
// ── XTerm.Terminal (the "engine", reached ONLY via model.Terminal.Engine) ──────────────────
//   Buffer -> XTerm.Buffer.TerminalBuffer (== SvcSystems Terminal.Buffer, same object)
//   Cols, Rows, Title, ActiveBuffer (XTerm.Common.BufferType), IsAlternateBufferActive
//   Many terminal-mode flags as bool props: InsertMode, ApplicationCursorKeys,
//   ApplicationKeypad, BracketedPasteMode, OriginMode, CursorVisible, ReverseWraparound,
//   ReverseVideo, SendFocusEvents, Win32InputMode, EightBitInput, MetaSendsEscape,
//   AltSendsEscape
//   CurrentDirectory, CurrentHyperlink, HyperlinkId, MouseTrackingMode, MouseEncoding
//   Selection -> XTerm.Selection.SelectionManager
//   ★ THE TERMINAL-REPLY PATH: event EventHandler<TerminalEvents.DataEventArgs> DataReceived
//     — raised when the terminal itself needs to talk back to the host process (e.g. a Device
//     Status Report / Cursor Position Report reply to a DSR/CPR query the remote app sent).
//     TerminalEvents.DataEventArgs.Data is a `string` (not bytes) — a consumer forwarding this
//     to a PTY must UTF8-encode it. This is reachable ONLY through Engine — neither the
//     SvcSystems.UI.Terminal.Terminal wrapper nor TerminalControlModel re-expose it, and it is
//     NOT the same as TerminalControlModel.UserInput (that one is for user-originated
//     keyboard/mouse input; DataReceived is for terminal-originated protocol replies). A full
//     wiring needs BOTH: UserInput for keystrokes, Engine.DataReceived for terminal replies.
//   Other events: CursorStyleChanged, TitleChanged, BellRang, Resized, Scrolled, LineFed,
//   DirectoryChanged, HyperlinkChanged, WindowMoved, WindowResized, WindowMinimized,
//   WindowMaximized, WindowRestored, WindowRaised, WindowLowered, WindowRefreshed,
//   WindowFullscreened, WindowInfoRequested, BufferChanged
//   Write(string data) / WriteLine(string data)  — raw feed entrypoints (wrapper's Feed calls
//                                                 Write under the hood).
//   Resize(int cols, int rows), Reset(), Clear()
//   ScrollLines(int), ScrollToTop(), ScrollToBottom()
//   GetLine(int line) -> string                  — `_buffer.Lines[line]?.TranslateToString
//                                                 (trimRight: true) ?? ""`. ⚠ `line` indexes
//                                                 buffer.Lines ABSOLUTELY, not the viewport — it
//                                                 only equals the on-screen row when YDisp == 0
//                                                 (no scrollback in play), which holds for this
//                                                 transcript test.
//   GetVisibleLines() -> string[]                 — Rows entries, GetLine(Buffer.YDisp + i).
//   GenerateKeyInput(Key, KeyModifiers = None) -> string
//   GenerateCharInput(char, KeyModifiers = None) -> string
//   GenerateMouseEvent(MouseButton, x, y, MouseEventType, KeyModifiers = None) -> string
//   GenerateFocusEvent(bool focused) -> string     — these four are the engine-side keyboard/
//                                                 mouse → escape-sequence generators; a host
//                                                 (e.g. TerminalControl's key handler) calls one
//                                                 of these to turn a keypress into bytes, then
//                                                 raises TerminalControlModel.UserInput/Send(...)
//                                                 with the result — the engine itself has no
//                                                 "user input" event of its own.
//   SetCursorStyle(CursorStyle, bool blink), SwitchToAltBuffer()/SwitchToNormalBuffer(), Dispose()
//
// ── XTerm.Buffer.TerminalBuffer (engine.Buffer) ─────────────────────────────────────────────
//   ViewportY, BaseY, Length, IsAtBottom, Cols, Rows, YDisp, YBase, Y, X, ScrollTop,
//   ScrollBottom, Lines -> CircularList<BufferLine>, SavedCursorState (nested SavedCursor:
//   X, Y, Attr, Charset), event Action<int> Trimmed
//   GetLine(int y) -> BufferLine?                 — buffer-local, distinct from Terminal.
//                                                 GetLine(int), which returns a string.
//   GetBlankLine, ScrollUp/ScrollDown/ScrollDisp/ScrollToLine/ScrollToBottom/ScrollToTop/
//   ClearScrollback/ScrollLines/SetScrollRegion/ResetScrollRegion/GetAbsoluteY/Resize/
//   SetCursor/SetCursorRaw, PrintViewport() -> string
//
// ── XTerm.Buffer.CircularList<T> (Lines) ────────────────────────────────────────────────────
//   this[int index] -> T?  (⚠ nullable — null-check or null-forgive before indexing further)
//   Length, MaxLength, Push/Pop/Splice/TrimStart/ShiftElements/Recycle/Clear/Resize/GetItems()
//
// ── XTerm.Buffer.BufferLine : IEnumerable<BufferCell> ───────────────────────────────────────
//   Length, IsWrapped, LineAttribute, IsDoubleWidth, Cache
//   this[int index] -> BufferCell                 — the per-cell accessor (non-nullable struct).
//   SetCell, GetCodePoint(int), Resize, Fill, CopyCellsFrom
//   TranslateToString(bool trimRight = false, int startCol = 0, int endCol = -1) -> string
//   GetTrimmedLength(), Clone(), CopyFrom(), GetEnumerator()
//
// ── XTerm.Buffer.BufferCell (struct) ────────────────────────────────────────────────────────
//   Content: string, Width: int, Attributes: AttributeData, CodePoint: int
//   static Empty, static Space; ctors (), (string content, int width, AttributeData attrs),
//   (int codePoint, int width, AttributeData attrs); IsEmpty(), IsSpace(), equality members.
//   Wide characters: first cell has Width == 2 and the glyph; the second cell has Width == 0 as
//   a placeholder (skip it when rendering, per XTerm.NET's own README).
//
// ── XTerm.Buffer.AttributeData (struct) ─────────────────────────────────────────────────────
//   Fg: int, Bg: int, Extended: int
//   ⚠ EXACT BIT LAYOUT (decompiled — the README's "0/1/2 mode" story is real but easy to
//   misapply; verified live with a scratch probe against `\x1b[1;31mR`, see below):
//     - Default (no color set) sentinel: Fg = 256, Bg = 257, Extended = 0 (static Default /
//       parameterless ctor both set this). This is NOT "mode 0" — it's a magic index value.
//     - GetFgColor()/GetBgColor()  = Fg/Bg & 0x1FFFFFF        (low 25 bits: the color index)
//     - GetFgColorMode()/GetBgColorMode() = Fg/Bg >> 25       (high bits: 0 = legacy/16-color
//       index, 1 = 256-color palette, 2 = true-color RGB — this axis is ONLY about how to
//       interpret a non-default index, not whether a color is set at all)
//     - SetFgColor(color, mode=0)/SetBgColor(color, mode=0) = (mode << 25) | (color & 0x1FFFFFF)
//     - Basic ANSI SGR colors (30–37 fg / 40–47 bg) land as GetFgColorMode() == 0 (legacy) with
//       GetFgColor() == the 0–7 index (SGR 31 "red" → GetFgColor() == 1). ⚠ So "was a color set"
//       must be tested as `GetFgColor() != 256` (or Bg != 257), NOT `GetFgColorMode() != 0` —
//       mode 0 is both the legacy-index case AND indistinguishable-by-mode-alone from unset.
//       Verified live: feeding "\x1b[1;31mR" then reading cell(0,0).Attributes gives
//       Fg=1, Bg=257, Extended=1, IsBold()=True, GetFgColor()=1, GetFgColorMode()=0.
//   IsBold()/IsDim()/IsItalic()/IsUnderline()/IsBlink()/IsInverse()/IsInvisible()/
//   IsStrikethrough()/IsOverline() read bits 0,1,2,3,4,5,6,7,8 of Extended respectively
//   (+ matching Set*(bool) mutators bit-flagging the same field).
//
// ── SvcSystems.UI.Terminal.TerminalControl (Avalonia visual; NOT used by this headless test)
//   Model, SelectedText, HasSelection, RightClickAction, IsMouseModeActive, FontFamily,
//   FontSize, CaretBrush, SelectionBrush, SelectAll(), CopySelection(), CopySelectionAsync(),
//   Paste(string), PasteFromClipboardAsync(), Search(string), SelectNextSearchResult(),
//   SelectPreviousSearchResult(), event ContextRequested
//
// Package/license: both packages are net-standard MIT-licensed (SvcSystems.UI.Terminal per its
// GitHub repo's codecov badge and MIT LICENSE; XTerm.NET's README states "License: MIT"
// explicitly). See task-8-report.md for the full `dotnet list ... --include-transitive` graph.
//
// ── WHAT THE ALT-SCREEN ASSERTIONS DO AND DON'T PROVE (fixed after review; read before reusing
//    this transcript pattern in Tasks 11/12) ────────────────────────────────────────────────
//   The transcript is fed in two pieces specifically so alt-screen ENTRY can be pinned, not
//   just exit: `alternateActiveDuringAltScreen` is read immediately after the \x1b[?1049h
//   prefix, before any suffix bytes are fed. The FIRST version of this test only asserted
//   `IsAlternateBufferActive == false` at the very end and `mainBufferText.Contains("back")`
//   — both of those hold identically whether \x1b[?1049h/l are interpreted OR silently
//   no-op'd (proven empirically: stripping the 1049 codes still ends with
//   IsAlternateBufferActive == false and "back" present, because "back" is fed unconditionally
//   after where \x1b[?1049l would have been). That version was NOT exercising alt-screen
//   support at all — it would have passed against a build with the alt-buffer switch
//   completely deleted. Proven now:
//     1. Entry:     alternateActiveDuringAltScreen is asserted TRUE right after \x1b[?1049h.
//     2. Exit:      isAlternateBufferActiveAfter is asserted FALSE after \x1b[?1049l.
//     3. Isolation: mainBufferText.DoesNotContain("alt") — the alt-buffer's own text must NOT
//        leak into the main buffer once switched back (with the codes stripped, "alt" DOES
//        appear in mainBufferText, since everything then lands in the one buffer:
//        "    mid alt  back"). This is what actually confirms the model is using two distinct
//        buffer objects, not just flipping a flag.
//   Still NOT proven by this test: alt-buffer content itself (what "mid" writes to the alt
//   screen render as, its own cursor/attribute state, or that re-entering alt-screen a second
//   time clears it) — only that alt-buffer writes don't bleed into the main buffer and that the
//   flag flips correctly around one enter/leave cycle. A future test touching alt-buffer
//   *content* (not just isolation) should read `engine.Buffer` while `IsAlternateBufferActive`
//   is still true (i.e. mid-transcript, the same way `alternateActiveDuringAltScreen` is
//   captured here), since `Terminal.Engine.Buffer`/`GetLine` always reflect whichever buffer is
//   CURRENTLY active — there is no separate always-addressable "the alt buffer" accessor once
//   you've switched back.

/// The acceptance gate for emulation fidelity: a captured ANSI/TUI stream
/// (colors, cursor addressing, alternate screen) through the decode-and-feed
/// path, ReflowOnResize=false per upstream's own TUI guidance.
[NotInParallel("AvaloniaSession")]
public class TerminalTranscriptTests {
    [Test]
    public async Task A_recorded_tui_transcript_feeds_without_faulting_and_lands_expected_cells() {
        var (row0Text, row0Bold, row0FgColor, midCells, alternateActiveDuringAltScreen,
                isAlternateBufferActiveAfter, mainBufferText) =
            await AvaloniaSession.DispatchAsync(() => {
                var model = new TerminalControlModel(new TerminalOptions {
                    Cols = 80,
                    Rows = 24,
                    ReflowOnResize = false,
                });
                var decoder = new Utf8StreamDecoder();
                var engine = model.Terminal.Engine;

                // transcript, split at the alt-screen boundary so ENTRY can be pinned (not just
                // exit): prefix ends with \x1b[?1049h; suffix carries the alt-only text, the
                // \x1b[?1049l exit, and the post-exit text.
                var prefix = "\x1b[2J\x1b[H\x1b[1;31mRED\x1b[0m\x1b[10;5Hmid\x1b[?1049h"u8.ToArray();
                var suffix = " alt \x1b[?1049l back"u8.ToArray();

                foreach (var chunk in Chunk(prefix, 7)) // deliberately ugly boundaries
                    model.Feed(decoder.Decode(chunk));
                var alternateActiveDuringAltScreen = engine.IsAlternateBufferActive;

                foreach (var chunk in Chunk(suffix, 7)) // deliberately ugly boundaries
                    model.Feed(decoder.Decode(chunk));
                model.Feed(decoder.Flush());

                var row0 = engine.Buffer.Lines[0]!;
                var row9 = engine.Buffer.Lines[9]!;
                var mid = new[] { row9[4].Content, row9[5].Content, row9[6].Content };

                var mainText = string.Join('\n', Enumerable.Range(0, engine.Rows).Select(engine.GetLine));

                return (
                    row0Text: engine.GetLine(0),
                    row0Bold: row0[0].Attributes.IsBold(),
                    row0FgColor: row0[0].Attributes.GetFgColor(),
                    midCells: mid,
                    alternateActiveDuringAltScreen,
                    isAlternateBufferActiveAfter: engine.IsAlternateBufferActive,
                    mainBufferText: mainText);
            });

        await Assert.That(row0Text).IsEqualTo("RED");
        await Assert.That(row0Bold).IsTrue();
        await Assert.That(row0FgColor).IsEqualTo(1); // SGR 31 -> legacy palette index 1 (red)
        await Assert.That(midCells).IsEquivalentTo(["m", "i", "d"], CollectionOrdering.Matching);
        await Assert.That(alternateActiveDuringAltScreen).IsTrue();  // alt-screen ENTRY pinned
        await Assert.That(isAlternateBufferActiveAfter).IsFalse();   // alt-screen EXIT pinned
        await Assert.That(mainBufferText).Contains("back");
        // The alt-buffer's own text must NOT leak into the main buffer once we've switched
        // back — this is the isolation check the original version of this test was missing
        // (it passed identically whether \x1b[?1049h/l were interpreted or silently no-op'd).
        await Assert.That(mainBufferText).DoesNotContain("alt");
    }

    static IEnumerable<byte[]> Chunk(byte[] data, int size) {
        for (var i = 0; i < data.Length; i += size) yield return data[i..Math.Min(i + size, data.Length)];
    }

    // ── Task 11: XtermTerminalSurface wiring ─────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_device_status_query_produces_a_terminal_reply_through_InputProduced() {
        // The engine answers a DSR (Device Status Report / cursor-position query) on its own,
        // via Terminal.Engine.DataReceived -- reachable only through XtermTerminalSurface
        // subscribing on the raw engine object (Task 8 discovery), not the wrapper or model.
        var replyContainsCursorPositionReport = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            var replies = new List<byte[]>();
            surface.InputProduced += replies.Add;

            surface.Feed("\x1b[6n"); // DSR: report cursor position

            return replies.Any(r => System.Text.Encoding.ASCII.GetString(r).Contains(";1R"));
        });

        await Assert.That(replyContainsCursorPositionReport).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Programmatic_input_on_the_model_raises_InputProduced() {
        // TerminalControlModel has no headless-testable keyboard event of its own (TerminalControl,
        // the Avalonia visual that turns real keypresses into Send(...) calls via
        // GenerateKeyInput/GenerateCharInput, is explicitly excluded from headless tests per Task 8
        // discovery). Model.Send(...) is the same UserInput event a real keypress handler raises,
        // so it's the correct headless-testable proxy for "keyboard input reaches InputProduced".
        var sawTypedByte = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            var replies = new List<byte[]>();
            surface.InputProduced += replies.Add;

            surface.Model.Send("a");

            return replies.Any(r => System.Text.Encoding.UTF8.GetString(r) == "a");
        });

        await Assert.That(sawTypedByte).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Feed_renders_text_into_the_model() {
        var line0 = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            surface.Feed("hello");
            return surface.Model.Terminal.Engine.GetLine(0);
        });

        await Assert.That(line0).IsEqualTo("hello");
    }
}
