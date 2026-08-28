using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Pi;

namespace Capacitor.Cli.SessionStartMemory;

internal static class SessionStartMemoryIdentity {
    public static string Create(HarnessId harness, string sessionId, string? lifecycleInstanceId) {
        var normalized = NormalizeSessionId(harness, sessionId)
            ?? throw new ArgumentException("A stable session identity is required.", nameof(sessionId));
        using var stream = new MemoryStream();
        stream.WriteByte(0x01);
        WritePresent(stream, harness.VendorId);
        WritePresent(stream, normalized);
        if (lifecycleInstanceId is null) stream.WriteByte(0x00);
        else WritePresent(stream, lifecycleInstanceId);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string? NormalizeSessionId(HarnessId harness, string? value) {
        if (string.IsNullOrEmpty(value)) return null;
        if (harness is HarnessId.Cursor or HarnessId.Copilot or HarnessId.Antigravity)
            return Guid.TryParse(value, out var guid) ? guid.ToString("N") : null;
        // Permissive canonicalisation: a GUID in any spelling (dashed, compact, braced, either case)
        // collapses to one identity, but a non-GUID id is still accepted verbatim rather than rejected.
        // Kiro needs both halves — its session_id is a GUID and its agentSpawn hook fires per prompt, so
        // two spellings across firings would mean two lease keys and a re-injected index; but it must not
        // fail closed on an id that does not parse, or a harness change would silently disable injection.
        if (harness is HarnessId.Claude or HarnessId.Kiro)
            return Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;
        if (harness == HarnessId.Pi)
            return PiSessionPathCanonicalizer.TryHash(value, out var hash) ? hash : null;
        return value;
    }

    static void WritePresent(Stream stream, string value) {
        stream.WriteByte(0x01);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}
