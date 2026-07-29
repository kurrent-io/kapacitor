using System.Buffers.Binary;
using System.Text;

namespace Capacitor.Cli.Core.LocalIpc;

/// Length-prefixed binary codec: [1B type][4B BE len][len payload]. No reflection /
/// JSON — AOT-safe and allocation-light. Spawn/Attached have structured payloads
/// (helpers below); other frames are trivial.
public static class FrameCodec {
    const int MaxPayload = 8 * 1024 * 1024; // hard cap; oversized => protocol error

    public static async Task WriteAsync(Stream s, LocalFrame f, CancellationToken ct) {
        var payload = Encode(f);
        var head = new byte[5];
        head[0] = (byte)f.Type;
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(1), payload.Length);
        await s.WriteAsync(head, ct);
        if (payload.Length > 0) await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    /// Returns null on clean EOF (peer closed between frames).
    public static async Task<LocalFrame?> ReadAsync(Stream s, CancellationToken ct) {
        var head = new byte[5];
        if (!await ReadExactlyOrEof(s, head, ct)) return null;
        var type = (FrameType)head[0];
        var len  = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (len is < 0 or > MaxPayload) throw new InvalidDataException($"frame len {len}");
        var payload = len == 0 ? [] : new byte[len];
        if (len > 0 && !await ReadExactlyOrEof(s, payload, ct))
            throw new EndOfStreamException("truncated frame payload");
        return Decode(type, payload);
    }

    static async Task<bool> ReadExactlyOrEof(Stream s, byte[] buf, CancellationToken ct) {
        var off = 0;
        while (off < buf.Length) {
            var n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return off != 0 ? throw new EndOfStreamException("truncated frame header") : false;
            off += n;
        }
        return true;
    }

    static byte[] Encode(LocalFrame f) => f.Type switch {
        FrameType.Stdin or FrameType.Stdout => f.Bytes,
        FrameType.Resize                    => Dims(f.Cols, f.Rows),
        FrameType.Detach or FrameType.List  => [],
        FrameType.Exited                    => BeInt(f.ExitCode),
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck
            or FrameType.Stop or FrameType.StopAck => Encoding.UTF8.GetBytes(f.Text),
        FrameType.Attached or FrameType.Spawn
            or FrameType.StopV2 or FrameType.AttachedReadOnly => f.Bytes, // pre-encoded by the helpers below
        _ => throw new InvalidDataException($"unencodable frame {f.Type}"),
    };

    static LocalFrame Decode(FrameType t, byte[] p) => t switch {
        FrameType.Stdin or FrameType.Stdout => new(t) { Bytes = p },
        FrameType.Resize  => new(t) { Cols = Be16(p, 0), Rows = Be16(p, 2) },
        FrameType.Detach or FrameType.List => new(t),
        FrameType.Exited  => new(t) { ExitCode = BinaryPrimitives.ReadInt32BigEndian(p) },
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck
            or FrameType.Stop or FrameType.StopAck => new(t) { Text = Encoding.UTF8.GetString(p) },
        FrameType.Attached or FrameType.Spawn
            or FrameType.StopV2 or FrameType.AttachedReadOnly => new(t) { Bytes = p },
        _ => throw new InvalidDataException($"undecodable frame {t}"),
    };

    // --- Spawn structured payload ---
    public static LocalFrame Spawn(string vendor, WorkLocation work, bool isPrivate, string cwd, IReadOnlyList<string> args, ushort cols, ushort rows) {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)work);
        WriteBe16(ms, cols); WriteBe16(ms, rows);
        WriteLp(ms, vendor); WriteLp(ms, cwd);
        WriteBe32(ms, args.Count);
        foreach (var a in args) WriteLp(ms, a);
        ms.WriteByte((byte)(isPrivate ? 1 : 0)); // APPENDED after args: older parsers ignore trailing bytes
        return new(FrameType.Spawn) { Bytes = ms.ToArray(), Text = vendor, Work = work, Cols = cols, Rows = rows };
    }
    public static string SpawnCwd(LocalFrame f) => ParseSpawn(f.Bytes).cwd;
    public static string[] SpawnArgs(LocalFrame f) => ParseSpawn(f.Bytes).args;
    public static (string vendor, WorkLocation work, bool isPrivate, string cwd, string[] args, ushort cols, ushort rows) Spawn(LocalFrame f)
        => ParseSpawn(f.Bytes);

    const int MaxSpawnArgs = 4096; // sane cap; the wire arg-count is untrusted (local 0600 socket, same-user)

    static (string vendor, WorkLocation work, bool isPrivate, string cwd, string[] args, ushort cols, ushort rows) ParseSpawn(byte[] p) {
        var o = 0;
        Require(p, o, 5); // work(1) + cols(2) + rows(2)
        var work = (WorkLocation)p[o++];
        var cols = Be16(p, o); o += 2; var rows = Be16(p, o); o += 2;
        var vendor = ReadLp(p, ref o); var cwd = ReadLp(p, ref o);

        Require(p, o, 4);
        var n = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o)); o += 4;
        // Validate the arg count against a cap AND remaining bytes before allocating, so a
        // malformed/huge count can't force an OOM (each arg has at least a 4-byte prefix).
        if (n is < 0 or > MaxSpawnArgs) throw new InvalidDataException($"Spawn arg count out of range: {n}");
        if ((long)(p.Length - o) < (long)n * 4) throw new InvalidDataException("Spawn arg count exceeds payload");

        var args = new string[n];
        for (var i = 0; i < n; i++) args[i] = ReadLp(p, ref o);
        // Trailing private flag (appended for wire-compat): absent (older CLI) => private=true,
        // the conservative default that preserves Phase-1 unregistered behaviour.
        var isPrivate = o >= p.Length || p[o] != 0;
        return (vendor, work, isPrivate, cwd, args, cols, rows);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless <paramref name="count"/> bytes
    /// remain at offset <paramref name="o"/> — keeps malformed frames a clean protocol error
    /// rather than an <c>ArgumentOutOfRangeException</c> or oversized allocation.</summary>
    static void Require(byte[] p, int o, int count) {
        if (o < 0 || count < 0 || (long)o + count > p.Length) throw new InvalidDataException("Frame payload truncated");
    }

    // --- Attached structured payload: [4B agentIdLen][agentId][snapshot...] ---
    public static LocalFrame Attached(string agentId, byte[] snapshot) {
        using var ms = new MemoryStream();
        WriteLp(ms, agentId);
        ms.Write(snapshot);
        return new(FrameType.Attached) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (string agentId, byte[] snapshot) Attached(LocalFrame f) {
        var o = 0; var id = ReadLp(f.Bytes, ref o);
        return (id, f.Bytes[o..]);
    }

    // --- StopV2 structured payload ---
    public static LocalFrame StopV2(bool force, string agentId) {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(force ? 1 : 0));
        WriteLp(ms, agentId);
        return new(FrameType.StopV2) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (bool force, string agentId) StopV2(LocalFrame f) {
        Require(f.Bytes, 0, 1);
        var o = 1;
        return (f.Bytes[0] == 1, ReadLp(f.Bytes, ref o));
    }

    // --- AttachedReadOnly structured payload ---
    // Length-prefixed id AND reason before the snapshot: the snapshot is the unbounded tail, so
    // anything appended after it would be painted onto the user's terminal instead of parsed.
    public static LocalFrame AttachedReadOnly(string agentId, string reason, byte[] snapshot) {
        using var ms = new MemoryStream();
        WriteLp(ms, agentId);
        WriteLp(ms, reason);
        ms.Write(snapshot);
        return new(FrameType.AttachedReadOnly) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (string agentId, string reason, byte[] snapshot) AttachedReadOnly(LocalFrame f) {
        var o = 0;
        var id = ReadLp(f.Bytes, ref o);
        var reason = ReadLp(f.Bytes, ref o);
        return (id, reason, f.Bytes[o..]);
    }

    // --- byte helpers ---
    static byte[] Dims(ushort c, ushort r) { var b = new byte[4]; BinaryPrimitives.WriteUInt16BigEndian(b, c); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), r); return b; }
    static byte[] BeInt(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    static ushort Be16(byte[] p, int o) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(o));
    static void WriteBe16(Stream s, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    static void WriteBe32(Stream s, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    static void WriteLp(Stream s, string v) { var u = Encoding.UTF8.GetBytes(v); WriteBe32(s, u.Length); s.Write(u); }
    static string ReadLp(byte[] p, ref int o) {
        Require(p, o, 4);
        var n = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o)); o += 4;
        if (n < 0) throw new InvalidDataException($"Negative length-prefix: {n}");
        Require(p, o, n);
        var v = Encoding.UTF8.GetString(p, o, n); o += n; return v;
    }
}
