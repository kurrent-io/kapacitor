namespace Capacitor.App.Services;

/// Minimal surface the VM drives; the production implementation (Task 11/12) wraps the
/// SvcSystems control model. Kept App-local (no Avalonia/SvcSystems types in the signature) so
/// TerminalTabViewModel's own tests never need a real terminal control.
public interface ITerminalSurface {
    /// Renders already-decoded text — callers own UTF-8 decoding (Utf8StreamDecoder) since the
    /// control's own byte-array Feed re-decodes fresh on every call and corrupts a code point
    /// split across frames.
    void Feed(string text);

    /// Keyboard input AND terminal-generated protocol replies (e.g. a DSR/CPR answer) — both
    /// need to reach the attached PTY, so the VM forwards this to SendInputAsync verbatim.
    event Action<byte[]>? InputProduced;

    /// User-driven resize (cols, rows) — the VM forwards this to ResizeAsync.
    event Action<int, int>? Resized;

    /// The surface's own current dimensions — read once, right after a read-write Attached, to
    /// correct the client's post-attach nudge (sent at RunAsync's phantom initial size, before the
    /// real pane size was ever known) to what the pane is actually showing.
    (int Cols, int Rows) CurrentSize { get; }

    /// Re-shows the terminal caret after the snapshot replay. Claude/codex TUIs hide the hardware
    /// cursor once at stream start and draw their own caret as an inverse-video cell — which the
    /// control currently paints invisibly (upstream: default-color sentinels resolve by draw-call
    /// position, so inverse-of-default is black-on-black). The engine's cursor position still
    /// tracks the TUI's caret, so forcing it visible renders a correctly placed caret instead.
    /// A TUI that hides the cursor again later is respected — this is a one-shot per attach.
    void EnsureCaretVisible();
}
