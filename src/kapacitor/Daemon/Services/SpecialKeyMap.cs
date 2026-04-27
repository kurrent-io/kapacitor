namespace kapacitor.Daemon.Services;

/// <summary>
/// Maps logical key names (sent over SignalR from UI clients) to the raw byte
/// sequences written to the PTY stdin. Returns an empty array for unknown keys
/// so the caller can no-op safely.
/// </summary>
public static class SpecialKeyMap {
    public static byte[] ToBytes(string key) => key switch {
        "Escape"    => [0x1b],
        "Tab"       => [0x09],
        "Enter"     => [0x0d],
        "CtrlC"     => [0x03],
        "ArrowUp"   => [0x1b, 0x5b, 0x41],
        "ArrowDown" => [0x1b, 0x5b, 0x42],
        "ShiftTab"  => [0x1b, 0x5b, 0x5a],
        _           => []
    };
}
