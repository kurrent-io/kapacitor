using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryIdentity {
    public static string Create(SessionStartHarness harness, string sessionId, string? lifecycleInstanceId) {
        var normalized = NormalizeSessionId(harness, sessionId)
            ?? throw new ArgumentException("A stable session identity is required.", nameof(sessionId));
        using var stream = new MemoryStream();
        stream.WriteByte(0x01);
        WritePresent(stream, HarnessToken(harness));
        WritePresent(stream, normalized);
        if (lifecycleInstanceId is null) stream.WriteByte(0x00);
        else WritePresent(stream, lifecycleInstanceId);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string? NormalizeSessionId(SessionStartHarness harness, string? value) {
        if (string.IsNullOrEmpty(value)) return null;
        if (harness is SessionStartHarness.Cursor or SessionStartHarness.Copilot or SessionStartHarness.Antigravity)
            return Guid.TryParse(value, out var guid) ? guid.ToString("N") : null;
        if (harness == SessionStartHarness.Claude)
            return Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;
        if (harness == SessionStartHarness.Pi)
            return PiSessionPathCanonicalizer.TryHash(value, out var hash) ? hash : null;
        return value;
    }

    public static string HarnessToken(SessionStartHarness harness) => harness switch {
        SessionStartHarness.Claude => "claude",
        SessionStartHarness.Codex => "codex",
        SessionStartHarness.Cursor => "cursor",
        SessionStartHarness.Copilot => "copilot",
        SessionStartHarness.Gemini => "gemini",
        SessionStartHarness.Kiro => "kiro",
        SessionStartHarness.Pi => "pi",
        SessionStartHarness.OpenCode => "opencode",
        SessionStartHarness.Antigravity => "antigravity",
        _ => throw new ArgumentOutOfRangeException(nameof(harness))
    };

    static void WritePresent(Stream stream, string value) {
        stream.WriteByte(0x01);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}
