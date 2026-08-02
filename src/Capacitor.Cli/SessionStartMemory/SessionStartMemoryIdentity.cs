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

    /// <summary>
    /// The key of the durable CONTEXT GENERATION counter for a session — a different namespace from the
    /// lease keys, so a generation record can never collide with a lease record.
    /// </summary>
    public static string CreateGenerationKey(SessionStartHarness harness, string sessionId) {
        var normalized = NormalizeSessionId(harness, sessionId)
            ?? throw new ArgumentException("A stable session identity is required.", nameof(sessionId));
        using var stream = new MemoryStream();
        stream.WriteByte(0x02);                       // 0x01 is the lease namespace; never reuse it here
        WritePresent(stream, HarnessToken(harness));
        WritePresent(stream, normalized);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// The lease key for a context generation.
    ///
    /// <para><b>Generation zero returns the LEGACY key, byte for byte.</b> The lease key already hashed an
    /// optional lifecycle instance id with an explicit absent-marker, and every harness passed null, so
    /// generation zero is exactly what a pre-generation CLI wrote. That is what makes this safe to ship
    /// across the already-merged adapters without a dual-read migration: a newer hook firing into a session
    /// started under the old CLI computes the same key, finds the completed lease, and stays silent.</para>
    /// </summary>
    public static string CreateForGeneration(SessionStartHarness harness, string sessionId, int generation) {
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));

        return Create(harness, sessionId, generation == 0 ? null : $"g{generation}");
    }

    public static string? NormalizeSessionId(SessionStartHarness harness, string? value) {
        if (string.IsNullOrEmpty(value)) return null;
        if (harness is SessionStartHarness.Cursor or SessionStartHarness.Copilot or SessionStartHarness.Antigravity)
            return Guid.TryParse(value, out var guid) ? guid.ToString("N") : null;
        // Permissive canonicalisation: a GUID in any spelling (dashed, compact, braced, either case)
        // collapses to one identity, but a non-GUID id is still accepted verbatim rather than rejected.
        // Kiro needs both halves — its session_id is a GUID and its agentSpawn hook fires per prompt, so
        // two spellings across firings would mean two lease keys and a re-injected index; but it must not
        // fail closed on an id that does not parse, or a harness change would silently disable injection.
        if (harness is SessionStartHarness.Claude or SessionStartHarness.Kiro)
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
